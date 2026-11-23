using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PonceanoFacturador.Services;

public sealed class ListaLocalRncService
{
    private static readonly IReadOnlyDictionary<string, string> Inicial = new Dictionary<string, string>
    {
        ["132585437"] = "PONCEANO MULTISERVICIOS, S.R.L.",
        ["132140372"] = "INVERSIONES Y CONSTRUCCIONES RODRIGUEZ INCOROD, SRL"
    };
    private readonly string _rutaJson;
    private readonly string _rutaCsv;
    private readonly Func<Task<Stream>>? _abrirCsvInicial;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public string RutaCsv => _rutaCsv;

    public ListaLocalRncService(string rutaJson, string? rutaCsv = null, Func<Task<Stream>>? abrirCsvInicial = null)
    {
        _rutaJson = rutaJson;
        _rutaCsv = rutaCsv ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rutaJson))!, "Lista_RNC_Proveedores.csv");
        _abrirCsvInicial = abrirCsvInicial;
    }

    public static string NormalizarRnc(string? rnc)
    {
        var valor = (rnc ?? string.Empty).Trim();
        return Regex.IsMatch(valor, @"^[0-9 \-]+$") ? valor.Replace("-", "").Replace(" ", "") : string.Empty;
    }

    public static bool EsRncValido(string? rnc) => Regex.IsMatch(NormalizarRnc(rnc), @"^(?:[0-9]{9}|[0-9]{11})$");

    public static string ResolverRazonSocial(string? nombreOcr, string? rnc, IReadOnlyDictionary<string, string> lista, out bool desdeLista)
    {
        ArgumentNullException.ThrowIfNull(lista);
        desdeLista = false;
        if (!string.IsNullOrWhiteSpace(nombreOcr)) return nombreOcr;
        if (lista.TryGetValue(NormalizarRnc(rnc), out var nombre))
        {
            desdeLista = true;
            return nombre;
        }
        return string.Empty;
    }

    public async Task<Dictionary<string, string>> CargarAsync()
    {
        await _mutex.WaitAsync();
        try { return await LeerAsync(); }
        finally { _mutex.Release(); }
    }

    private async Task<Dictionary<string, string>> LeerAsync()
    {
        var resultado = new Dictionary<string, string>(Inicial, StringComparer.Ordinal);
        if (File.Exists(_rutaJson))
        {
            await using var archivo = File.OpenRead(_rutaJson);
            var guardados = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(archivo)
                ?? throw new InvalidDataException("La lista local de RNC está vacía o tiene un formato inválido.");
            foreach (var (rnc, nombre) in guardados)
            {
                if (!EsRncValido(rnc) || string.IsNullOrWhiteSpace(nombre))
                    throw new InvalidDataException("La lista local contiene un RNC o una razón social inválidos.");
                resultado[NormalizarRnc(rnc)] = nombre.Trim();
            }
        }

        await AsegurarCsvAsync();
        var contenido = await File.ReadAllTextAsync(_rutaCsv, Encoding.UTF8);
        foreach (var (rnc, nombre) in ParsearCsv(contenido)) resultado[rnc] = nombre;
        return resultado;
    }

    private async Task AsegurarCsvAsync()
    {
        var carpeta = Path.GetDirectoryName(Path.GetFullPath(_rutaCsv))!;
        Directory.CreateDirectory(carpeta);
        if (_abrirCsvInicial is not null)
        {
            await using var origen = await _abrirCsvInicial();
            using var lector = new StreamReader(origen, Encoding.UTF8, true);
            var datosEmpaquetados = ParsearCsv(await lector.ReadToEndAsync());
            var datosLocales = File.Exists(_rutaCsv)
                ? ParsearCsv(await File.ReadAllTextAsync(_rutaCsv, Encoding.UTF8))
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var necesitaGuardar = !File.Exists(_rutaCsv);
            foreach (var (rnc, nombre) in datosEmpaquetados)
            {
                if (!datosLocales.TryGetValue(rnc, out var nombreLocal) || !string.Equals(nombreLocal, nombre, StringComparison.Ordinal))
                {
                    datosLocales[rnc] = nombre;
                    necesitaGuardar = true;
                }
            }
            if (necesitaGuardar) await GuardarCsvAsync(datosLocales);
            return;
        }
        if (!File.Exists(_rutaCsv))
            await GuardarCsvAsync(Inicial.ToDictionary(par => par.Key, par => par.Value, StringComparer.Ordinal));
    }

    private static Dictionary<string, string> ParsearCsv(string contenido)
    {
        if (string.IsNullOrWhiteSpace(contenido)) throw new InvalidDataException("El archivo Lista_RNC_Proveedores.csv está vacío.");
        var delimitador = DetectarDelimitador(contenido);
        var registros = ParsearRegistros(contenido, delimitador);
        var resultado = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var indice = 0; indice < registros.Count; indice++)
        {
            var fila = registros[indice];
            if (fila.All(string.IsNullOrWhiteSpace)) continue;
            if (indice == 0 && fila.Count >= 2 && fila[0].Trim().TrimStart('\uFEFF').Contains("RNC", StringComparison.OrdinalIgnoreCase)) continue;
            if (fila.Count < 2) throw new InvalidDataException($"La fila {indice + 1} del CSV debe tener RNC y razón social.");
            var rnc = NormalizarRnc(fila[0]);
            var nombre = fila[1].Trim();
            if (!EsRncValido(rnc) || string.IsNullOrWhiteSpace(nombre))
                throw new InvalidDataException($"La fila {indice + 1} del CSV contiene un RNC o una razón social inválidos.");
            resultado[rnc] = nombre;
        }
        if (resultado.Count == 0) throw new InvalidDataException("El CSV no contiene registros válidos de RNC y razón social.");
        return resultado;
    }

    private static char DetectarDelimitador(string contenido)
    {
        var comas = 0;
        var puntosYComas = 0;
        var entreComillas = false;
        for (var indice = 0; indice < contenido.Length; indice++)
        {
            var caracter = contenido[indice];
            if (caracter == '"')
            {
                if (entreComillas && indice + 1 < contenido.Length && contenido[indice + 1] == '"') indice++;
                else entreComillas = !entreComillas;
            }
            else if (!entreComillas && (caracter is '\r' or '\n')) break;
            else if (!entreComillas && caracter == ',') comas++;
            else if (!entreComillas && caracter == ';') puntosYComas++;
        }
        if (comas == 0 && puntosYComas == 0) throw new InvalidDataException("No se pudo detectar el separador del CSV; usa coma o punto y coma.");
        return puntosYComas > comas ? ';' : ',';
    }

    private static List<List<string>> ParsearRegistros(string contenido, char delimitador)
    {
        var registros = new List<List<string>>();
        var registro = new List<string>();
        var campo = new StringBuilder();
        var entreComillas = false;
        for (var indice = 0; indice < contenido.Length; indice++)
        {
            var caracter = contenido[indice];
            if (entreComillas)
            {
                if (caracter == '"' && indice + 1 < contenido.Length && contenido[indice + 1] == '"') { campo.Append('"'); indice++; }
                else if (caracter == '"') entreComillas = false;
                else campo.Append(caracter);
            }
            else if (caracter == '"') entreComillas = true;
            else if (caracter == delimitador) { registro.Add(campo.ToString()); campo.Clear(); }
            else if (caracter is '\r' or '\n')
            {
                registro.Add(campo.ToString()); campo.Clear(); registros.Add(registro); registro = new List<string>();
                if (caracter == '\r' && indice + 1 < contenido.Length && contenido[indice + 1] == '\n') indice++;
            }
            else campo.Append(caracter);
        }
        if (entreComillas) throw new InvalidDataException("El CSV contiene un campo entre comillas sin cerrar.");
        if (campo.Length > 0 || registro.Count > 0) { registro.Add(campo.ToString()); registros.Add(registro); }
        return registros;
    }

    private async Task GuardarCsvAsync(IReadOnlyDictionary<string, string> lista)
    {
        var carpeta = Path.GetDirectoryName(Path.GetFullPath(_rutaCsv))!;
        Directory.CreateDirectory(carpeta);
        var temporal = Path.Combine(carpeta, $"rnc-{Guid.NewGuid():N}.tmp");
        try
        {
            var contenido = new StringBuilder("RNC,Razon Social\r\n");
            foreach (var (rnc, nombre) in lista.OrderBy(par => par.Key, StringComparer.Ordinal))
                contenido.Append(CampoCsv(rnc)).Append(',').Append(CampoCsv(nombre)).Append("\r\n");
            await File.WriteAllTextAsync(temporal, contenido.ToString(), new UTF8Encoding(false));
            File.Move(temporal, _rutaCsv, true);
        }
        finally { if (File.Exists(temporal)) File.Delete(temporal); }
    }

    private static string CampoCsv(string valor) => "\"" + valor.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public async Task<Dictionary<string, string>> AgregarAsync(string rnc, string razonSocial)
    {
        if (!EsRncValido(rnc)) throw new ArgumentException("El RNC o cédula debe tener 9 u 11 dígitos.", nameof(rnc));
        ArgumentException.ThrowIfNullOrWhiteSpace(razonSocial);
        rnc = NormalizarRnc(rnc);
        await _mutex.WaitAsync();
        try
        {
            var lista = await LeerAsync();
            if (lista.ContainsKey(rnc)) throw new InvalidOperationException("Este RNC ya existe en el CSV local; no se modificó el nombre guardado.");
            lista.Add(rnc, razonSocial.Trim());
            await GuardarCsvAsync(lista);
            return lista;
        }
        finally { _mutex.Release(); }
    }
}
