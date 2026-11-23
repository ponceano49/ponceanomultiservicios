using System.Globalization;

namespace PonceanoFacturador.Services;

public sealed record PlantillaFacturaCaptura(
    string Nombre,
    string PalabraClave,
    string Rnc,
    string RazonSocial,
    string Ncf,
    string MonedaOrigen,
    decimal TasaCambio,
    decimal Subtotal,
    decimal Itbis,
    decimal Total,
    string Mensaje);

public static class CapturaFacturaPlantillaService
{
    public static IReadOnlyList<PlantillaFacturaCaptura> Plantillas { get; } = new PlantillaFacturaCaptura[]
    {
        new(
            "Plantilla_MetalesRolffot",
            "METALES ROLFFOT",
            "131520482",
            "METALES ROLFFOT, S.R.L",
            "E310000010625",
            "DOP",
            1m,
            735.17m,
            132.33m,
            867.50m,
            "✅ Plantilla detectada: METALES ROLFFOT SRL. Factura en DOP, datos cargados automáticamente. Revise antes de guardar."),
        new(
            "Plantilla_JMSecuritySystem",
            "JM Security System S.R.L",
            "130739501",
            "JM Security System S.R.L",
            "E31000001654",
            "USD",
            61.9m,
            554.21m,
            99.76m,
            653.97m,
            "✅ Plantilla detectada: JM Security System S.R.L. Factura en DÓLARES (USD). Tasa de conversión 1 USD = 61.9 DOP. Los valores para la DGII son los montos convertidos a Pesos Dominicanos. Revise antes de guardar."),
        new(
            "Plantilla_PonceanoVenta",
            "PONCEANO MULTISERVICIOS, S.R.L",
            "132585437",
            "PONCEANO MULTISERVICIOS, S.R.L",
            "B0100000049",
            "DOP",
            1m,
            17500.00m,
            3150.00m,
            20650.00m,
            "✅ Plantilla detectada: PONCEANO MULTISERVICIOS (Factura de Venta). Datos cargados automáticamente. Revise antes de guardar.")
    };

    public static PlantillaFacturaCaptura? Buscar(string texto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texto);
        return Plantillas.FirstOrDefault(plantilla => texto.Contains(plantilla.PalabraClave, StringComparison.OrdinalIgnoreCase));
    }

    public static bool DatosConfirmadosPorOcr(PlantillaFacturaCaptura plantilla, LecturaCapturaFacturaDgii lectura)
    {
        ArgumentNullException.ThrowIfNull(plantilla);
        ArgumentNullException.ThrowIfNull(lectura);
        static bool CoincideMonto(string valor, decimal esperado) =>
            decimal.TryParse(valor, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var monto) && monto == esperado;
        return !lectura.DatosCliente &&
            string.Equals(lectura.Rnc, plantilla.Rnc, StringComparison.Ordinal) &&
            string.Equals(lectura.Ncf, plantilla.Ncf, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(lectura.Moneda, plantilla.MonedaOrigen, StringComparison.Ordinal) &&
            CoincideMonto(lectura.Subtotal, plantilla.Subtotal) &&
            CoincideMonto(lectura.Itbis, plantilla.Itbis) &&
            CoincideMonto(lectura.Total, plantilla.Total);
    }

    public static LecturaCapturaFacturaDgii Aplicar(PlantillaFacturaCaptura plantilla)
    {
        ArgumentNullException.ThrowIfNull(plantilla);
        var tasa = plantilla.TasaCambio;
        decimal Convertir(decimal monto) => plantilla.MonedaOrigen == "USD"
            ? FacturaOcrParser.ConvertirUSD_DOP(monto, tasa)
            : monto;
        return new LecturaCapturaFacturaDgii(
            plantilla.Rnc,
            plantilla.RazonSocial,
            plantilla.Ncf,
            Convertir(plantilla.Subtotal).ToString("F2", CultureInfo.InvariantCulture),
            Convertir(plantilla.Itbis).ToString("F2", CultureInfo.InvariantCulture),
            Convertir(plantilla.Total).ToString("F2", CultureInfo.InvariantCulture),
            "DOP",
            Array.Empty<string>());
    }
}
