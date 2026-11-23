using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PonceanoFacturador.Services;

public sealed record ResultadoValidacion606(IReadOnlyList<string> Errores, IReadOnlyList<string> Observaciones)
{
    public bool ListoParaEnvio => Errores.Count == 0;
}

public static class ValidacionArchivo606Service
{
    private const string EncabezadoInterno = "RNC|NCF|NCF_Modificado|Fecha_Emision|Tipo_Bien_Servicio|Monto_Facturado|ITBIS|Otros_Impuestos|Total|Condicion_Pago";

    public static async Task<ResultadoValidacion606> ValidarAsync(Stream archivo, string rncContribuyente, int anio, int mes)
    {
        ArgumentNullException.ThrowIfNull(archivo);
        var errores = new List<string>();
        var observaciones = new List<string>();
        if (!Regex.IsMatch(rncContribuyente ?? string.Empty, @"^(?:[0-9]{9}|[0-9]{11})$"))
            errores.Add("El RNC o cédula del contribuyente debe contener 9 u 11 dígitos.");
        if (anio is < 2000 or > 9998 || mes is < 1 or > 12)
            errores.Add("Seleccione un período válido.");

        using var lector = new StreamReader(archivo, new UTF8Encoding(false, true), true, leaveOpen: true);
        var cabecera = await lector.ReadLineAsync();
        if (cabecera is null)
            errores.Add("El archivo está vacío.");
        else if (cabecera.TrimStart('\uFEFF') == EncabezadoInterno)
        {
            errores.Add("El archivo es un borrador interno de PonceanoFacturador, no un TXT 606 oficial. Debe convertirse y validarse con la herramienta o especificación vigente de la DGII.");
            var numero = 1;
            var cantidad = 0;
            string? linea;
            while ((linea = await lector.ReadLineAsync()) is not null)
            {
                numero++;
                if (string.IsNullOrWhiteSpace(linea)) continue;
                cantidad++;
                var campos = linea.Split('|');
                if (campos.Length != 10)
                {
                    errores.Add($"Línea {numero}: se esperaban 10 columnas del borrador interno.");
                    continue;
                }
                if (!Regex.IsMatch(campos[0], @"^(?:[0-9]{9}|[0-9]{11})$")) errores.Add($"Línea {numero}: RNC/cédula inválido.");
                if (!Regex.IsMatch(campos[1], @"^(?:[AB][0-9]{10}|E[0-9]{12}|[ACDF-Z][0-9]{19}|[A-Z0-9]{3}-[0-9]{3,4}-[0-9]{7,10})$")) errores.Add($"Línea {numero}: NCF inválido.");
                if (!DateTime.TryParseExact(campos[3], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha) || fecha.Year != anio || fecha.Month != mes)
                    errores.Add($"Línea {numero}: fecha inválida o fuera del período seleccionado.");
                if (string.IsNullOrWhiteSpace(campos[4])) errores.Add($"Línea {numero}: falta el tipo de bien o servicio.");
                for (var i = 5; i <= 8; i++)
                    if (!decimal.TryParse(campos[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var monto) || monto < 0 || decimal.Round(monto, 2) != monto)
                        errores.Add($"Línea {numero}: monto en columna {i + 1} inválido.");
            }
            if (cantidad == 0) errores.Add("El borrador no contiene registros.");
            observaciones.Add($"Se revisaron {cantidad} registros del borrador interno; esta revisión no certifica el formato oficial.");
        }
        else
            errores.Add("No se reconoce el formato de este archivo. La validación oficial del 606 aún no está implementada; no se puede certificar para envío.");
        return new ResultadoValidacion606(errores, observaciones);
    }
}
