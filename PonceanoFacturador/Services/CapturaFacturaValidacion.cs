using System.Globalization;
using System.Text.RegularExpressions;

namespace PonceanoFacturador.Services;

public static class CapturaFacturaValidacion
{
    public static void Validar(CapturaFacturaDgii factura)
    {
        ArgumentNullException.ThrowIfNull(factura);
        var rnc = factura.Rnc.Trim();
        var ncf = factura.Ncf.Trim().ToUpperInvariant();
        var razon = factura.RazonSocial.Trim();
        if (!Regex.IsMatch(rnc, "^(?:[0-9]{9}|[0-9]{11})$")) throw new InvalidOperationException("Introduce el RNC o cédula del proveedor (9 u 11 dígitos).");
        if (string.IsNullOrWhiteSpace(razon)) throw new InvalidOperationException("Introduce la razón social del proveedor.");
        if (!Regex.IsMatch(ncf, @"^(?:[AB][0-9]{10}|E[0-9]{12}|[ACDF-Z][0-9]{19}|[A-Z0-9]{3}-[0-9]{3,4}-[0-9]{7,10})$")) throw new InvalidOperationException("Introduce un NCF válido.");
        var subtotal = LeerMonto(factura.Subtotal, "subtotal");
        var itbis = LeerMonto(factura.Itbis, "ITBIS");
        var total = LeerMonto(factura.Total, "total");
        if (total <= 0 || subtotal + itbis != total) throw new InvalidOperationException("El total debe ser mayor que cero y coincidir con subtotal más ITBIS.");
        if (factura.Moneda is not ("DOP" or "USD")) throw new InvalidOperationException("Selecciona la moneda de la factura.");
        factura.Rnc = rnc;
        factura.Ncf = ncf;
        factura.RazonSocial = razon;
        factura.Subtotal = subtotal.ToString("F2", CultureInfo.InvariantCulture);
        factura.Itbis = itbis.ToString("F2", CultureInfo.InvariantCulture);
        factura.Total = total.ToString("F2", CultureInfo.InvariantCulture);
    }

    public static string? MotivoRevisionAutomatica(bool datosCliente, bool proveedorValidado, bool plantillaDetectada)
    {
        if (datosCliente) return "Los datos corresponden al cliente y requieren confirmación del proveedor.";
        if (!proveedorValidado) return "El proveedor no pudo validarse contra el padrón local.";
        if (plantillaDetectada) return "La plantilla contiene datos fijos de una factura; verifica el NCF y los importes contra el documento antes de guardar.";
        return null;
    }

    private static decimal LeerMonto(string texto, string nombre)
    {
        if (!decimal.TryParse(texto.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var monto) || monto < 0 || decimal.Round(monto, 2) != monto)
            throw new InvalidOperationException($"Introduce un {nombre} válido, no negativo y con hasta dos decimales.");
        return monto;
    }
}
