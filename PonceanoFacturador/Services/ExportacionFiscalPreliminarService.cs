using System.Globalization;
using System.Text;
using PonceanoFacturador.Models;

namespace PonceanoFacturador.Services;

public sealed record ResultadoExportacionFiscal(bool Valido, string Ruta, int CantidadRegistros, IReadOnlyList<string> Errores);
public sealed record ProblemaRegistroFiscal(RegistroFiscal Registro, IReadOnlyList<string> Errores);

public sealed class ExportacionFiscalPreliminarService
{
    private static readonly string[] Encabezados606 = { "RNC", "NCF", "NCF_Modificado", "Fecha_Emision", "Tipo_Bien_Servicio", "Monto_Facturado", "ITBIS", "Otros_Impuestos", "Total", "Condicion_Pago" };
    private static readonly string[] Encabezados607 = { "RNC_Cliente", "NCF", "NCF_Modificado", "Fecha_Emision", "Tipo_Ingreso", "Monto_Facturado", "ITBIS", "Otros_Impuestos", "Total", "Condicion_Pago" };
    private readonly DatabaseService _db;
    private readonly Func<string> _outputDirectory;

    public ExportacionFiscalPreliminarService(DatabaseService db, Func<string>? outputDirectory = null)
    {
        _db = db;
        _outputDirectory = outputDirectory ?? (() => FileSystem.AppDataDirectory);
    }

    public async Task<ResultadoExportacionFiscal> GenerarAsync(string usuarioId, string tipo, int anio, int mes, bool incluirNotas, string? carpetaDestino = null)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("Inicia sesión antes de exportar.", nameof(usuarioId));
        if (tipo is not ("606" or "607")) throw new ArgumentException("El reporte debe ser 606 o 607.", nameof(tipo));
        if (mes is < 1 or > 12 || anio is < 2000 or > 9998) throw new ArgumentOutOfRangeException(nameof(mes), "Selecciona un período válido.");

        var inicio = new DateTime(anio, mes, 1, 0, 0, 0, DateTimeKind.Local);
        var fin = inicio.AddMonths(1);
        var operacion = tipo == "606" ? "Compra" : "Venta";
        var registros = await _db.ObtenerRegistrosFiscalesAsync(usuarioId, operacion, inicio, fin);
        if (!incluirNotas)
            registros = registros.Where(registro => string.IsNullOrWhiteSpace(registro.NcfModificado)).ToList();

        var errores = Validar(registros, inicio, fin);
        if (errores.Count != 0) return new ResultadoExportacionFiscal(false, string.Empty, registros.Count, errores);

        var encabezados = tipo == "606" ? Encabezados606 : Encabezados607;
        var filas = new List<string> { string.Join('|', encabezados) };
        foreach (var registro in registros)
        {
            var monto = Parsear(registro.MontoGravadoTotal) + Parsear(registro.MontoExento);
            var total = Parsear(registro.TotalFacturado);
            var itbis = Parsear(registro.ItbisFacturado);
            var otros = Parsear(registro.OtrosImpuestos);
            var tipoFiscal = tipo == "606" ? registro.TipoBienServicio : registro.TipoIngresoGasto;
            filas.Add(string.Join('|', new[]
            {
                Campo(registro.RncContraparte), Campo(registro.Ncf), Campo(registro.NcfModificado),
                registro.FechaComprobante.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture), Campo(tipoFiscal),
                monto.ToString("0.00", CultureInfo.InvariantCulture), itbis.ToString("0.00", CultureInfo.InvariantCulture),
                otros.ToString("0.00", CultureInfo.InvariantCulture), total.ToString("0.00", CultureInfo.InvariantCulture), Campo(registro.CondicionPago)
            }));
        }

        var nombre = $"{tipo}-interno-{anio}{mes:D2}-{Guid.NewGuid():N}.txt";
        var carpeta = carpetaDestino ?? Path.Combine(_outputDirectory(), "ExportacionesDGII");
        Directory.CreateDirectory(carpeta);
        var ruta = Path.Combine(carpeta, nombre);
        await File.WriteAllTextAsync(ruta, string.Join(Environment.NewLine, filas), new UTF8Encoding(false));
        await _db.RegistrarExportacionDGIIAsync(new HistorialExportacionDGII
        {
            TipoReporte = tipo, PeriodoMes = mes, PeriodoAnio = anio, IdUsuario = usuarioId,
            NombreArchivo = nombre, RutaGuardado = ruta, CantidadRegistros = registros.Count,
            Estado = "Borrador interno; no validado como formato oficial DGII",
            ObservacionesDGII = "Exportación preliminar basada en los campos internos. Revisar y convertir al diseño vigente publicado por DGII antes de presentar."
        });
        return new ResultadoExportacionFiscal(true, ruta, registros.Count, Array.Empty<string>());
    }

    public async Task<IReadOnlyList<ProblemaRegistroFiscal>> ObtenerProblemasAsync(string usuarioId, string tipo, int anio, int mes)
    {
        if (tipo is not ("606" or "607")) throw new ArgumentException("El reporte debe ser 606 o 607.", nameof(tipo));
        var inicio = new DateTime(anio, mes, 1, 0, 0, 0, DateTimeKind.Local);
        var fin = inicio.AddMonths(1);
        var registros = await _db.ObtenerRegistrosFiscalesAsync(usuarioId, tipo == "606" ? "Compra" : "Venta", inicio, fin);
        return registros.Select(registro => new ProblemaRegistroFiscal(registro, ValidarRegistro(registro, inicio, fin)))
            .Where(problema => problema.Errores.Count > 0).ToArray();
    }

    private static List<string> Validar(IEnumerable<RegistroFiscal> registros, DateTime inicio, DateTime fin)
    {
        var lista = registros.ToList();
        var errores = lista.SelectMany(registro => ValidarRegistro(registro, inicio, fin)).ToList();
        if (errores.Count == 0 && lista.Count == 0) errores.Add("No hay registros fiscales para el período seleccionado.");
        return errores;
    }

    private static List<string> ValidarRegistro(RegistroFiscal registro, DateTime inicio, DateTime fin)
    {
        var errores = new List<string>();
        var ncf = registro.Ncf.Trim();
        var rnc = new string(registro.RncContraparte.Where(char.IsAsciiDigit).ToArray());
        var fecha = registro.FechaComprobante.ToLocalTime();
        var etiqueta = string.IsNullOrWhiteSpace(ncf) ? registro.Id : ncf;
        if (ncf.Length < 11 || ncf.Any(char.IsWhiteSpace)) errores.Add($"{etiqueta}: NCF vacío o con longitud/formato no reconocido.");
        if (rnc.Length is not (9 or 11)) errores.Add($"{etiqueta}: RNC/cédula debe tener 9 u 11 dígitos.");
        if (fecha < inicio || fecha >= fin) errores.Add($"{etiqueta}: la fecha no pertenece al período seleccionado.");
        if (!TryMonto(registro.MontoGravadoTotal, out _) || !TryMonto(registro.MontoExento, out _) ||
            !TryMonto(registro.ItbisFacturado, out _) || !TryMonto(registro.OtrosImpuestos, out _) || !TryMonto(registro.TotalFacturado, out _))
            errores.Add($"{etiqueta}: faltan montos fiscales numéricos; completa la revisión del registro antes de exportar.");
        var clasificacion = registro.TipoOperacion == "Compra" ? registro.TipoBienServicio : registro.TipoIngresoGasto;
        if (string.IsNullOrWhiteSpace(clasificacion) || clasificacion.Contains("Pendiente", StringComparison.OrdinalIgnoreCase))
            errores.Add($"{etiqueta}: falta clasificar el tipo fiscal requerido antes de exportar.");
        return errores;
    }

    private static bool TryMonto(string valor, out decimal monto) =>
        decimal.TryParse(valor, NumberStyles.Number, CultureInfo.InvariantCulture, out monto);

    private static decimal Parsear(string valor) =>
        decimal.TryParse(valor, NumberStyles.Number, CultureInfo.InvariantCulture, out var monto) ? monto : 0m;

    private static string Campo(string valor) =>
        (valor ?? string.Empty).Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
