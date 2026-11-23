using System.Globalization;
using System.Text.RegularExpressions;

namespace PonceanoFacturador.Services;

public sealed record LecturaCapturaFacturaDgii(
    string Rnc,
    string RazonSocial,
    string Ncf,
    string Subtotal,
    string Itbis,
    string Total,
    string Moneda,
    IReadOnlyList<string> Problemas,
    bool DatosCliente = false);

public static class CapturaFacturaDgiiParser
{
    private static readonly Regex NcfRegex = new(@"(?<![A-Z0-9])(?<ncf>(?:[AB]\d{2}[ \t\r\n]*\d{8}|E\d{2}[ \t\r\n]*\d{10}|[ACDF-Z]\d{2}[ \t\r\n]*\d{17}|[A-Z0-9]{3}-\d{3,4}-\d{7,10}))(?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static LecturaCapturaFacturaDgii Analizar(string texto, string rncCompania)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texto);
        var rncEmpresa = Regex.Replace(rncCompania ?? string.Empty, @"\D", string.Empty);
        var cliente = Regex.Match(texto, @"(?im)^[ \t]*CLIENTE[ \t]*[:#-][ \t]*(?<nombre>[^\r\n]*)");
        var identificaciones = Regex.Matches(texto, @"(?im)^[ \t]*(?:R[ \t]*\.?[ \t]*N[ \t]*\.?[ \t]*C\.?)(?:[ \t]*/[ \t]*C[EÉ]DULA)?[ \t]*(?:DEL?[ \t]+)?(?:(?<rol>EMISOR|PROVEEDOR|CLIENTE|COMPRADOR|RECEPTOR)[ \t]*)?[:#-]?[ \t]*(?<valor>\d(?:[ \t-]*\d){8,10})(?![\d-])")
            .Select(m => (Valor: Regex.Replace(m.Groups["valor"].Value, @"\D", string.Empty), Rol: m.Groups["rol"].Value, Posicion: m.Index))
            .Where(item => item.Valor.Length is 9 or 11 && item.Valor != rncEmpresa).ToList();
        var proveedores = identificaciones.Where(item => item.Rol.Equals("EMISOR", StringComparison.OrdinalIgnoreCase) || item.Rol.Equals("PROVEEDOR", StringComparison.OrdinalIgnoreCase)).ToList();
        var candidatos = proveedores.Count > 0 ? proveedores : identificaciones;
        var rncProveedor = candidatos.Select(item => item.Valor).Distinct(StringComparer.Ordinal).Count() == 1 ? candidatos[0].Valor : string.Empty;
        var datosCliente = proveedores.Count == 0 && candidatos.Count > 0 &&
            (candidatos.All(item => item.Rol.Equals("CLIENTE", StringComparison.OrdinalIgnoreCase) || item.Rol.Equals("COMPRADOR", StringComparison.OrdinalIgnoreCase) || item.Rol.Equals("RECEPTOR", StringComparison.OrdinalIgnoreCase)) ||
             cliente.Success && candidatos.All(item => item.Posicion > cliente.Index));

        var razon = Regex.Match(texto, @"(?im)^[ \t]*(?:RAZ[ÓO]N[ \t]+SOCIAL|NOMBRE[ \t]+(?:DEL[ \t]+)?(?:PROVEEDOR|EMISOR)|PROVEEDOR|EMISOR)[ \t]*[:#-][ \t]*(?<nombre>[^\r\n]+)");
        var razonSocial = datosCliente ? cliente.Groups["nombre"].Value.Trim() : razon.Success ? razon.Groups["nombre"].Value.Trim() : string.Empty;
        var ncf = NcfRegex.Match(texto);
        var ncfLeido = ncf.Success ? Regex.Replace(ncf.Groups["ncf"].Value, @"\s", string.Empty).ToUpperInvariant() : string.Empty;
        var monedaUsd = Regex.IsMatch(texto, @"(?<![A-Z])(?:US\s*\$|USD|D[ÓO]LARES?)(?![A-Z])", RegexOptions.IgnoreCase);
        var monedaDop = Regex.IsMatch(texto, @"(?<![A-Z])(?:RD\s*\$|DOP|PESOS?)(?![A-Z])", RegexOptions.IgnoreCase);
        var moneda = monedaUsd && monedaDop ? "AMBIGUA" : monedaUsd ? "USD" : "DOP";

        var subtotal = LeerMonto(texto, @"(?:SUB[ \t-]*TOTAL|BASE[ \t]+IMPONIBLE)");
        var itbis = LeerMonto(texto, @"(?:ITBIS|I\.?T\.?B\.?I\.?S\.?|IVA)");
        var total = LeerMonto(texto, @"(?:TOTAL(?:[ \t]+(?:A[ \t]+PAGAR|GENERAL|FACTURA))?|VALOR[ \t]+TOTAL)");
        if (subtotal is null && itbis is null && total is null)
            (subtotal, itbis, total) = LeerTotalesEnColumna(texto);
        var problemas = new List<string>();
        if (string.IsNullOrWhiteSpace(rncProveedor)) problemas.Add("RNC del proveedor");
        if (string.IsNullOrWhiteSpace(razonSocial)) problemas.Add("Razón social");
        if (string.IsNullOrWhiteSpace(ncfLeido)) problemas.Add("NCF");
        if (subtotal is null) problemas.Add("Subtotal");
        if (itbis is null) problemas.Add("ITBIS");
        if (total is null || total <= 0) problemas.Add("Total");
        if (monedaUsd && monedaDop) problemas.Add("Moneda ambigua");
        if (subtotal.HasValue && itbis.HasValue && total.HasValue && decimal.Abs(subtotal.Value + itbis.Value - total.Value) > 0.02m)
            problemas.Add("Los importes no coinciden");

        return new LecturaCapturaFacturaDgii(rncProveedor, razonSocial, ncfLeido,
            Formatear(subtotal), Formatear(itbis), Formatear(total), moneda, problemas, datosCliente);
    }

    private static (decimal? Subtotal, decimal? Itbis, decimal? Total) LeerTotalesEnColumna(string texto)
    {
        var etiquetas = Regex.Match(texto, @"(?im)^[ \t]*SUB[ \t-]*TOTAL[ \t]*:?[ \t]*\r?\n(?:[ \t]*\r?\n)*[ \t]*ITBIS[ \t]*:?[ \t]*\r?\n(?:[ \t]*\r?\n)*[ \t]*TOTAL[ \t]*:?[ \t]*\r?$");
        if (!etiquetas.Success) return (null, null, null);
        var columnas = Regex.Matches(texto[etiquetas.Index..], @"(?im)^[ \t]*(?:RD[ \t]*S?[ \t]*\$|US[ \t]*\$|DOP|USD)[ \t]*(?<monto>\d[\d.,]*)[ \t]*\r?$")
            .Select(m => ParseMonto(m.Groups["monto"].Value)).ToArray();
        if (columnas.Length != 3 || columnas.Any(monto => monto is null)) return (null, null, null);
        var subtotal = columnas[0]!.Value;
        var itbis = columnas[1]!.Value;
        var total = columnas[2]!.Value;
        return total > 0 && Math.Abs(subtotal + itbis - total) <= 0.02m ? (subtotal, itbis, total) : (null, null, null);
    }

    private static decimal? LeerMonto(string texto, string etiqueta)
    {
        var coincidencias = Regex.Matches(texto, $@"(?im)^[ \t]*{etiqueta}\b[^\r\n]*?(?<monto>\d[\d., \u00A0]*)[ \t]*\r?$");
        return coincidencias.Count == 0 ? null : ParseMonto(coincidencias[^1].Groups["monto"].Value);
    }

    private static decimal? ParseMonto(string texto)
    {
        var valor = texto.Replace(" ", string.Empty).Replace("\u00A0", string.Empty);
        var coma = valor.LastIndexOf(',');
        var punto = valor.LastIndexOf('.');
        if (coma >= 0 && punto >= 0)
        {
            var decimalSeparador = coma > punto ? ',' : '.';
            valor = valor.Replace(decimalSeparador == ',' ? "." : ",", string.Empty);
            if (decimalSeparador == ',') valor = valor.Replace(',', '.');
        }
        else if (coma >= 0)
            valor = valor.Length - coma - 1 is 1 or 2 ? valor.Replace(',', '.') : valor.Replace(",", string.Empty);
        else if (punto >= 0 && valor.Length - punto - 1 == 3)
            valor = valor.Replace(".", string.Empty);

        return decimal.TryParse(valor, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var monto) && monto >= 0
            ? monto
            : null;
    }

    private static string Formatear(decimal? monto) => monto?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty;
}
