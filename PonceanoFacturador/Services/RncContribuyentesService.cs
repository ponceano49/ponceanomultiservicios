using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SQLite;

namespace PonceanoFacturador.Services;

[Table("RncContribuyentes")]
public sealed class RncContribuyenteLocal
{
    [PrimaryKey]
    public string Rnc { get; set; } = string.Empty;
    public string RazonSocial { get; set; } = string.Empty;
    [Indexed]
    public string RazonNormalizada { get; set; } = string.Empty;
}

[Table("RncContribuyentesImportacion")]
public sealed class RncContribuyentesImportacion
{
    [PrimaryKey]
    public int Id { get; set; } = 1;
    public long LongitudCsv { get; set; }
    public long FechaModificacionTicks { get; set; }
    public int Registros { get; set; }
    public int RegistrosOmitidos { get; set; }
}

public sealed class RncContribuyentesService : IAsyncDisposable
{
    private const string NombreZip = "RNC_CONTRIBUYENTES.zip";
    private const string NombreCsv = "RNC_Contribuyentes_Actualizado_14_Nov_2026.csv";
    private const int TamanoLote = 5000;
    private static readonly Encoding CsvEncoding;

    static RncContribuyentesService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CsvEncoding = Encoding.GetEncoding(1252);
    }
    private readonly SQLiteAsyncConnection _db;
    private readonly string _carpetaCsv;
    private readonly Func<Task<Stream>> _abrirZip;
    private readonly SemaphoreSlim _inicializacion = new(1, 1);
    private bool _inicializado;

    public RncContribuyentesService(string rutaDb, string carpetaCsv, Func<Task<Stream>> abrirZip)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(rutaDb))!);
        _db = new SQLiteAsyncConnection(rutaDb);
        _carpetaCsv = carpetaCsv;
        _abrirZip = abrirZip;
    }

    public async Task InicializarAsync(IProgress<string>? progreso = null)
    {
        if (_inicializado) return;
        await _inicializacion.WaitAsync();
        try
        {
            if (_inicializado) return;
            await _db.CreateTableAsync<RncContribuyenteLocal>();
            await _db.CreateTableAsync<RncContribuyentesImportacion>();
            var csv = await AsegurarCsvLocalAsync(progreso);
            var info = new FileInfo(csv);
            var importacion = await _db.Table<RncContribuyentesImportacion>().Where(item => item.Id == 1).FirstOrDefaultAsync();
            var cantidad = await _db.Table<RncContribuyenteLocal>().CountAsync();
            if (importacion is null || importacion.LongitudCsv != info.Length || importacion.FechaModificacionTicks != info.LastWriteTimeUtc.Ticks || importacion.Registros != cantidad)
                await ImportarCsvAsync(csv, info, progreso);
            _inicializado = true;
            var estado = await _db.Table<RncContribuyentesImportacion>().Where(item => item.Id == 1).FirstOrDefaultAsync();
            progreso?.Report(estado is { RegistrosOmitidos: > 0 }
                ? $"⚠️ Listado RNC listo: {estado.Registros:N0} contribuyentes; se omitieron {estado.RegistrosOmitidos:N0} filas con RNC o razón social no válidos."
                : $"Listado RNC listo: {await _db.Table<RncContribuyenteLocal>().CountAsync():N0} contribuyentes.");
        }
        finally { _inicializacion.Release(); }
    }

    public async Task<string?> BuscarPorRncAsync(string? rnc)
    {
        await InicializarAsync();
        var normalizado = ListaLocalRncService.NormalizarRnc(rnc);
        if (!ListaLocalRncService.EsRncValido(normalizado)) return null;
        var contribuyente = await _db.Table<RncContribuyenteLocal>().Where(item => item.Rnc == normalizado).FirstOrDefaultAsync();
        return contribuyente?.RazonSocial;
    }

    public async Task<IReadOnlyList<(string Rnc, string RazonSocial)>> BuscarPorRazonSocialAsync(string? razonSocial)
    {
        await InicializarAsync();
        var normalizada = NormalizarRazonSocial(razonSocial);
        if (normalizada.Length == 0) return Array.Empty<(string, string)>();
        var coincidencias = await _db.Table<RncContribuyenteLocal>()
            .Where(item => item.RazonNormalizada == normalizada)
            .Take(2)
            .ToListAsync();
        return coincidencias.Select(item => (item.Rnc, item.RazonSocial)).ToList();
    }

    private async Task<string> AsegurarCsvLocalAsync(IProgress<string>? progreso)
    {
        Directory.CreateDirectory(_carpetaCsv);
        var ruta = Path.Combine(_carpetaCsv, NombreCsv);
        if (File.Exists(ruta)) return ruta;
        progreso?.Report("Preparando el listado local de contribuyentes...");
        var temporal = ruta + ".tmp";
        try
        {
            await using var zipStream = await _abrirZip();
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var entrada = zip.GetEntry(NombreCsv) ?? throw new InvalidDataException($"El archivo {NombreCsv} no existe dentro de {NombreZip}.");
            await using (var origen = entrada.Open())
            await using (var destino = new FileStream(temporal, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await origen.CopyToAsync(destino);
            File.Move(temporal, ruta, true);
            return ruta;
        }
        finally { if (File.Exists(temporal)) File.Delete(temporal); }
    }

    private async Task ImportarCsvAsync(string ruta, FileInfo info, IProgress<string>? progreso)
    {
        progreso?.Report("Indexando el padrón local de RNC; la primera carga puede tardar unos minutos...");
        await _db.DeleteAllAsync<RncContribuyenteLocal>();
        var lote = new List<RncContribuyenteLocal>(TamanoLote);
        var registros = 0;
        var registrosOmitidos = 0;
        var lineaNumero = 0;
        using var lector = new StreamReader(ruta, CsvEncoding, false, 1024 * 1024);
        while (await lector.ReadLineAsync() is { } linea)
        {
            lineaNumero++;
            if (lineaNumero == 1 && linea.TrimStart('\uFEFF').StartsWith("RNC,", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(linea)) continue;
            var campos = LeerDosCampos(linea);
            var rnc = campos.Rnc.Trim();
            var razon = campos.Razon.Trim();
            if (!ListaLocalRncService.EsRncValido(rnc) || razon.Length == 0)
            {
                registrosOmitidos++;
                continue;
            }
            lote.Add(new RncContribuyenteLocal { Rnc = rnc, RazonSocial = razon, RazonNormalizada = NormalizarRazonSocial(razon) });
            if (lote.Count < TamanoLote) continue;
            await GuardarLoteAsync(lote);
            registros += lote.Count;
            lote.Clear();
            if (registros % 50000 == 0) progreso?.Report($"Indexados {registros:N0} contribuyentes...");
        }
        if (lote.Count > 0)
        {
            await GuardarLoteAsync(lote);
            registros += lote.Count;
        }
        if (registros == 0) throw new InvalidDataException("El padrón local no contiene registros de contribuyentes.");
        var registrosIndexados = await _db.Table<RncContribuyenteLocal>().CountAsync();
        await _db.InsertOrReplaceAsync(new RncContribuyentesImportacion
        {
            Id = 1,
            LongitudCsv = info.Length,
            FechaModificacionTicks = info.LastWriteTimeUtc.Ticks,
            Registros = registrosIndexados,
            RegistrosOmitidos = registrosOmitidos
        });
    }

    private Task GuardarLoteAsync(List<RncContribuyenteLocal> lote) =>
        _db.RunInTransactionAsync(connection =>
        {
            foreach (var registro in lote) connection.InsertOrReplace(registro);
        });

    private static (string Rnc, string Razon) LeerDosCampos(string linea)
    {
        var campos = new List<string>(2);
        var campo = new StringBuilder();
        var entreComillas = false;
        for (var indice = 0; indice < linea.Length; indice++)
        {
            var caracter = linea[indice];
            if (entreComillas)
            {
                if (caracter == '"' && indice + 1 < linea.Length && linea[indice + 1] == '"') { campo.Append('"'); indice++; }
                else if (caracter == '"') entreComillas = false;
                else campo.Append(caracter);
            }
            else if (caracter == '"') entreComillas = true;
            else if (caracter == ',')
            {
                campos.Add(campo.ToString());
                campo.Clear();
                if (campos.Count == 2) break;
            }
            else campo.Append(caracter);
        }
        if (campos.Count == 1) campos.Add(campo.ToString());
        if (campos.Count < 2) throw new InvalidDataException("Una fila del padrón no tiene las columnas RNC y razón social.");
        return (campos[0].Trim(), campos[1].Trim());
    }

    public ValueTask DisposeAsync() => new(_db.CloseAsync());

    public static string NormalizarRazonSocial(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return string.Empty;
        var descompuesta = valor.Trim().Normalize(NormalizationForm.FormD);
        var resultado = new StringBuilder(descompuesta.Length);
        foreach (var caracter in descompuesta)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(caracter)) resultado.Append(char.ToUpperInvariant(caracter));
        }
        return resultado.ToString();
    }
}
