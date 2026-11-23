using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PonceanoFacturador.Services;

public sealed record DocumentoFiscalOcr(string TipoOperacion, string RncEmisor, string RncContraparte, string NombreContraparte, string DireccionEmisor, string TelefonoEmisor, string Ncf, DateTime Fecha, decimal MontoOriginal, string MonedaOriginal, decimal Itbis, decimal Subtotal = 0, bool ClasificacionSupuesta = false);
public sealed record AnalisisMonedaOcr(string Moneda, decimal Tasa, bool TasaManual, bool MonedaSupuesta, decimal? Subtotal, decimal? Itbis, decimal? Total);

public static class FacturaOcrParser
{
    private static readonly Regex NcfRegex = new(@"(?<![A-Z0-9])(?<ncf>(?:[AB][0-9]{2}[ \t\r\n]*[0-9]{8}|E[0-9]{2}[ \t\r\n]*[0-9]{10}|[ACDF-Z][0-9]{2}[ \t\r\n]*[0-9]{17}|[A-Z0-9]{3}-[0-9]{3,4}-[0-9]{7,10}))(?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NcfNombreRegex = new(@"(?<ncf>(?:[AB][0-9]{2}[ \t\r\n]*[0-9]{8}|E[0-9]{2}[ \t\r\n]*[0-9]{10}|[ACDF-Z][0-9]{2}[ \t\r\n]*[0-9]{17}|[A-Z0-9]{3}-[0-9]{3,4}-[0-9]{7,10}))(?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RncRegex = new(@"\b(?:R[ \t]*\.?[ \t]*N[ \t]*\.?[ \t]*C\.?|IDENTIFICACION|CEDULA)(?:[ \t]*/[ \t]*CEDULA)?[ \t]*(?<role>(?:(?:DEL?|DE LA)[ \t]+)?(?:EMISOR|PROVEEDOR|CLIENTE|COMPRADOR|RECEPTOR))?[ \t]*[:#-]?[ \t]*(?<rnc>(?:[0-9](?:[ \t]*-?[ \t]*[0-9]){10}|[0-9](?:[ \t]*-?[ \t]*[0-9]){8}))(?![0-9]|[ \t-]+[0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TotalRegex = new(@"\b(?:TOTAL(?!\s*(?:PARCIAL|SUBTOTAL))(?:\s+(?:A\s+PAGAR|GENERAL|FACTURA|DEUDOR))?|VALOR\s+TOTAL)\b[^\d\r\n]{0,32}?(?<currency>US\$|RD\$|USD|DOP|\$)?\s*(?<amount>[\d][\d., \u00A0]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SubtotalRegex = new(@"\b(?:SUB[\s-]*TOTAL|BASE\s+IMPONIBLE|MONTO\s+GRAVADO)\b[^\d\r\n]{0,32}?(?:US\$|RD\$|USD|DOP|\$)?\s*(?<amount>[\d][\d., \u00A0]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ItbisRegex = new(@"\b(?:ITBIS|I\.?T\.?B\.?I\.?S\.?|IVA)\b(?:\s*\(\s*\d{1,2}\s*%\s*\)|\s+\d{1,2}\s*%)?[^\d\r\n]{0,24}(?:US\$|RD\$|USD|DOP|\$)?\s*(?<amount>[\d][\d., \u00A0]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"\b(\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}-\d{1,2}-\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex NombreEmisorRegex = new(@"\b(?:RAZON\s+SOCIAL|NOMBRE(?:\s+(?:DEL\s+)?(?:PROVEEDOR|EMISOR))?|PROVEEDOR|EMISOR)\s*[:\s]+(?<nombre>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NombreClienteRegex = new(@"\b(?:PARA|CLIENTE|FACTURAR\s+A|SENORES?)\s*[:#-]?\s*(?<nombre>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EtiquetasNombreRegex = new(@"\s+(?:RNC|RN[0I]|CEDULA|DIRECCION|TELEFONO)\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DireccionRegex = new(@"\b(?:DIRECCION|DOMICILIO)\s*[:\s]+(?<valor>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TelefonoRegex = new(@"\b(?:TELEFONO|TEL\.?|CELULAR)\s*[:\s]+(?<valor>[+\d() -]{7,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DigitsRegex = new(@"\D", RegexOptions.Compiled);
    private static readonly Regex TasaLineaRegex = new(@"(?im)^.*\b(?:TASA(?:\s+DE)?\s+CAMBIO|TIPO\s+DE\s+CAMBIO|CAMBIO|TASA\s+USD\s*(?:→|->|A|/)\s*DOP)\b[^\r\n]*$", RegexOptions.Compiled);
    private static readonly Regex TasaValorRegex = new(@"(?:[:=]|\s)\s*(?<valor>\d+(?:[.,]\d+)?)\s*$", RegexOptions.Compiled);
    private static readonly Regex DolarRegex = new(@"(?<![A-Z])(?:US\s*\$|USD|DOLARES?)(?![A-Z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PesoRegex = new(@"(?<![A-Z])(?:RD\s*\$|DOP|PESOS?|RD)(?![A-Z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string texto, string rncCompania, out DocumentoFiscalOcr? documento, out string error, string? rutaArchivo = null)
    {
        documento = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(texto))
        {
            error = "No se pudo extraer texto del archivo.";
            return false;
        }

        var limpio = Normalizar(texto);
        var ncf = NcfRegex.Match(limpio);
        if (!ncf.Success && !string.IsNullOrWhiteSpace(rutaArchivo))
            ncf = NcfNombreRegex.Match(Path.GetFileNameWithoutExtension(rutaArchivo));
        if (!ncf.Success)
        {
            error = "No se encontró un comprobante fiscal válido en el texto ni en el nombre del archivo.";
            return false;
        }

        var rncEtiquetados = RncRegex.Matches(limpio)
            .Select(m => (Rnc: SoloDigitos(m.Groups["rnc"].Value), Rol: NormalizarRol(m.Groups["role"].Value)))
            .Where(r => EsRnc(r.Rnc))
            .ToList();
        var candidatosRnc = rncEtiquetados.Select(r => r.Rnc).Distinct(StringComparer.Ordinal).ToList();
        var compania = SoloDigitos(rncCompania);
        var companiaEnDocumento = rncEtiquetados.FirstOrDefault(r => !string.IsNullOrEmpty(compania) && r.Rnc == compania);
        var emisorEtiquetado = rncEtiquetados.FirstOrDefault(r => r.Rol is "EMISOR" or "PROVEEDOR");
        string? rncEmisor;
        string tipo;
        var clasificacionSupuesta = false;

        if (!string.IsNullOrEmpty(emisorEtiquetado.Rnc) && emisorEtiquetado.Rnc != compania)
        {
            rncEmisor = emisorEtiquetado.Rnc;
            tipo = "Compra";
        }
        else if (companiaEnDocumento.Rnc is not null && (companiaEnDocumento.Rol is "CLIENTE" or "COMPRADOR" or "RECEPTOR"))
        {
            rncEmisor = candidatosRnc.FirstOrDefault(r => r != compania);
            tipo = "Compra";
        }
        else if (companiaEnDocumento.Rnc is not null && rncEtiquetados.Any(r => r.Rnc != compania && (r.Rol is "CLIENTE" or "COMPRADOR" or "RECEPTOR")))
        {
            rncEmisor = compania;
            tipo = "Venta";
        }
        else if (companiaEnDocumento.Rnc is not null && (companiaEnDocumento.Rol is "EMISOR" or "PROVEEDOR"))
        {
            rncEmisor = compania;
            tipo = "Venta";
        }
        else if (companiaEnDocumento.Rnc is not null && candidatosRnc.Count == 1)
        {
            rncEmisor = compania;
            tipo = "Venta";
        }
        else if (companiaEnDocumento.Rnc is not null && Regex.IsMatch(limpio, @"\b(COMPRA|PROVEEDOR|ADQUIRIENTE|COMPRADOR)\b", RegexOptions.IgnoreCase))
        {
            rncEmisor = candidatosRnc.FirstOrDefault(r => r != compania);
            tipo = "Compra";
        }
        else if (companiaEnDocumento.Rnc is not null && candidatosRnc.Count == 2 && rncEtiquetados.All(r => string.IsNullOrEmpty(r.Rol)))
        {
            rncEmisor = candidatosRnc[0];
            tipo = rncEmisor == compania ? "Venta" : "Compra";
            clasificacionSupuesta = true;
        }
        else
        {
            error = "No se pudo determinar si la compañía es emisora o compradora. Revisa los RNC identificados.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(rncEmisor))
        {
            error = "No se encontró el RNC del emisor o proveedor.";
            return false;
        }

        var fecha = DateRegex.Matches(limpio)
            .Select(m => ParseFecha(m.Groups[1].Value))
            .FirstOrDefault(f => f.HasValue);
        if (fecha is null)
        {
            error = "No se encontró una fecha válida.";
            return false;
        }

        var totalMatch = TotalRegex.Match(limpio);
        if (!totalMatch.Success || !ParseMonto(totalMatch.Groups["amount"].Value, out var total) || total <= 0)
        {
            error = "No se pudo leer un monto total válido.";
            return false;
        }

        string moneda;
        try { moneda = DetectarMoneda(limpio).Moneda; }
        catch (InvalidDataException ex) { error = ex.Message; return false; }
        var itbisMatch = ItbisRegex.Match(limpio);
        var subtotalMatch = SubtotalRegex.Match(limpio);
        decimal itbis = 0;
        decimal subtotalReportado = 0;
        if (itbisMatch.Success && !ParseMonto(itbisMatch.Groups["amount"].Value, out itbis))
        {
            error = "No se pudo leer un monto de ITBIS válido.";
            return false;
        }
        if (subtotalMatch.Success && !ParseMonto(subtotalMatch.Groups["amount"].Value, out subtotalReportado))
        {
            error = "No se pudo leer un subtotal válido.";
            return false;
        }
        if (itbis < 0 || itbis > total || subtotalReportado < 0 || subtotalReportado > total)
        {
            error = "El subtotal o el ITBIS leído no es consistente con el total.";
            return false;
        }
        if (!itbisMatch.Success)
        {
            error = "No se encontró el monto de ITBIS; no se puede inferir de la diferencia con el total.";
            return false;
        }
        var subtotal = subtotalMatch.Success ? subtotalReportado : total - itbis;
        if (subtotalMatch.Success && Math.Abs(subtotal + itbis - total) > 0.02m)
        {
            error = "El subtotal y el ITBIS no coinciden con el total; verifica descuentos u otros cargos.";
            return false;
        }

        var rncContraparte = candidatosRnc.FirstOrDefault(r => r != rncEmisor) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rncContraparte))
        {
            error = "No se encontró el RNC o la cédula del cliente o contraparte.";
            return false;
        }
        var nombreMatch = (tipo == "Venta" ? NombreClienteRegex : NombreEmisorRegex).Match(limpio);
        var nombreContraparte = nombreMatch.Success
            ? EtiquetasNombreRegex.Replace(nombreMatch.Groups["nombre"].Value, string.Empty).Trim(' ', ':', ';', ',')
            : string.Empty;
        var direccion = DireccionRegex.Match(limpio);
        var telefono = TelefonoRegex.Match(limpio);
        documento = new DocumentoFiscalOcr(tipo, rncEmisor, rncContraparte, nombreContraparte,
            direccion.Success ? direccion.Groups["valor"].Value.Trim() : string.Empty,
            telefono.Success ? telefono.Groups["valor"].Value.Trim() : string.Empty,
            ncf.Groups["ncf"].Value.Replace(" ", string.Empty).Replace("\t", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).ToUpperInvariant(), fecha.Value, total, moneda, itbis, subtotal, clasificacionSupuesta);
        return true;
    }

    private static (string Moneda, bool Supuesta) DetectarMoneda(string texto)
    {
        var sinTasas = TasaLineaRegex.Replace(texto, string.Empty);
        var dolares = DolarRegex.IsMatch(sinTasas);
        var pesos = PesoRegex.IsMatch(sinTasas);
        if (dolares && pesos)
            throw new InvalidDataException("La factura contiene indicadores tanto de dólares como de pesos; revisa la moneda antes de procesarla.");
        return dolares ? ("USD", false) : ("DOP", !pesos);
    }

    public static AnalisisMonedaOcr AnalizarMoneda(string texto, decimal? tasaManual)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texto);
        var limpio = Normalizar(texto);
        var (moneda, supuesta) = DetectarMoneda(limpio);
        decimal tasa = 1;
        var manual = false;
        if (moneda == "USD")
        {
            var tasas = new List<decimal>();
            foreach (Match linea in TasaLineaRegex.Matches(limpio))
            {
                var valor = TasaValorRegex.Match(linea.Value);
                if (!valor.Success || !decimal.TryParse(valor.Groups["valor"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var detectada) || detectada <= 0)
                    throw new InvalidDataException($"No se pudo interpretar una tasa de cambio válida en: {linea.Value.Trim()}");
                tasas.Add(detectada);
            }
            if (tasas.Distinct().Skip(1).Any())
                throw new InvalidDataException("La factura contiene tasas de cambio diferentes; revisa la tasa antes de procesarla.");
            if (tasas.Count > 0) tasa = tasas[0];
            else if (tasaManual is > 0) { tasa = tasaManual.Value; manual = true; }
            else throw new InvalidDataException("La factura está en dólares y no indica una tasa válida; introduce una tasa manual mayor que cero.");
            if (decimal.Round(tasa, 4) != tasa)
                throw new InvalidDataException("La tasa de cambio admite hasta cuatro decimales; revisa la factura o la tasa manual.");
        }
        static decimal? Leer(Regex regex, string valor)
        {
            var match = regex.Match(valor);
            return match.Success && ParseMonto(match.Groups["amount"].Value, out var monto) ? monto : null;
        }
        return new AnalisisMonedaOcr(moneda, tasa, manual, supuesta, Leer(SubtotalRegex, limpio), Leer(ItbisRegex, limpio), Leer(TotalRegex, limpio));
    }

    public static string ResumenMoneda(AnalisisMonedaOcr analisis)
    {
        ArgumentNullException.ThrowIfNull(analisis);
        if (analisis.Moneda == "DOP")
            return $"✅ Moneda: PESOS (DOP) — Tasa: 1.00 — Sin conversión{(analisis.MonedaSupuesta ? "\nMoneda asumida por ausencia de indicación; revisar." : string.Empty)}";
        var cultura = CultureInfo.GetCultureInfo("en-US");
        string Monto(string etiqueta, decimal? monto) => monto.HasValue
            ? $"{etiqueta} USD: {monto.Value.ToString("N2", cultura)} → DOP: {ConvertirUSD_DOP(monto.Value, analisis.Tasa).ToString("N2", cultura)}"
            : $"{etiqueta}: no detectado; requiere revisión.";
        var origen = analisis.TasaManual ? "⚠️ Usando tasa configurada manualmente. Tasa tomada de configuración manual" : "Tasa de cambio encontrada en la factura";
        return $"MONEDA DETECTADA: DÓLARES (USD)\n{origen}: {analisis.Tasa.ToString("0.00####", cultura)}\n{Monto("Subtotal", analisis.Subtotal)}\n{Monto("ITBIS", analisis.Itbis)}\n{Monto("Total", analisis.Total)}";
    }

    public static string? LeerFormaPagoExplicita(string texto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texto);
        var coincidencias = Regex.Matches(Normalizar(texto), @"(?im)^\s*(?:forma|metodo)\s+de\s+pago\s*[:\-]\s*(efectivo|transferencia|tarjeta|credito)\s*$")
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return coincidencias.Length == 1 ? coincidencias[0] switch
        {
            "EFECTIVO" => "Efectivo",
            "TRANSFERENCIA" => "Transferencia",
            "TARJETA" => "Tarjeta",
            "CREDITO" => "Crédito",
            _ => null
        } : null;
    }

    public static decimal ConvertirUSD_DOP(decimal montoUSD, decimal tasaCambioUSD_DOP)
    {
        if (tasaCambioUSD_DOP <= 0) throw new ArgumentOutOfRangeException(nameof(tasaCambioUSD_DOP), "La tasa de cambio debe ser mayor que cero.");
        return Math.Round(montoUSD * tasaCambioUSD_DOP, 2);
    }

    public static decimal ConvertirDOP_USD(decimal montoDOP, decimal tasaCambioUSD_DOP)
    {
        if (tasaCambioUSD_DOP <= 0) throw new ArgumentOutOfRangeException(nameof(tasaCambioUSD_DOP), "La tasa de cambio debe ser mayor que cero.");
        return Math.Round(montoDOP / tasaCambioUSD_DOP, 2);
    }

    private static DateTime? ParseFecha(string valor)
    {
        string[] formatos = { "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "yyyy-M-d", "yyyy-MM-dd", "d/M/yy", "dd/MM/yy", "d-M-yy", "dd-MM-yy" };
        return DateTime.TryParseExact(valor, formatos, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha)
            ? fecha.Date
            : null;
    }

    private static bool ParseMonto(string texto, out decimal valor)
    {
        var monto = texto.Replace(" ", string.Empty).Replace("\u00A0", string.Empty);
        var coma = monto.LastIndexOf(',');
        var punto = monto.LastIndexOf('.');
        if (coma >= 0 && punto >= 0)
        {
            var decimalSeparador = coma > punto ? ',' : '.';
            monto = monto.Replace(decimalSeparador == ',' ? "." : ",", string.Empty);
            if (decimalSeparador == ',') monto = monto.Replace(',', '.');
        }
        else if (coma >= 0)
        {
            var decimales = monto.Length - coma - 1;
            monto = decimales is 1 or 2 ? monto.Replace(',', '.') : monto.Replace(",", string.Empty);
        }
        else if (punto >= 0 && monto.Length - punto - 1 == 3)
        {
            monto = monto.Replace(".", string.Empty);
        }
        return decimal.TryParse(monto, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out valor);
    }

    private static string SoloDigitos(string valor) => DigitsRegex.Replace(valor, string.Empty);
    private static string NormalizarRol(string valor) => valor.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToUpperInvariant() ?? string.Empty;
    private static bool EsRnc(string valor) => valor.Length is 9 or 11;

    private static string Normalizar(string valor)
    {
        var sinAcentos = valor.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(sinAcentos.Length);
        foreach (var caracter in sinAcentos)
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) != UnicodeCategory.NonSpacingMark) builder.Append(caracter);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}