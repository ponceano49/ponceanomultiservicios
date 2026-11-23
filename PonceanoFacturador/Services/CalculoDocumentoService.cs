using PonceanoFacturador.Models;

namespace PonceanoFacturador.Services;

public static class CalculoDocumentoService
{
    public static (decimal Subtotal, decimal Itbis, decimal Total) Calcular(IEnumerable<ItemFactura> items, decimal porcentaje, bool impuestoIncluido)
    {
        if (porcentaje is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(porcentaje), "El impuesto debe estar entre 0 % y 100 %.");
        var filas = items.ToList();
        var totalIngresado = decimal.Round(filas.Sum(item => item.Subtotal), 2);
        var gravado = decimal.Round(filas.Where(item => item.SujetoImpuesto).Sum(item => item.Subtotal), 2);
        if (impuestoIncluido)
        {
            var subtotalGravado = decimal.Round(gravado / (1 + porcentaje / 100m), 2);
            var itbisIncluido = gravado - subtotalGravado;
            return (totalIngresado - itbisIncluido, itbisIncluido, totalIngresado);
        }
        var itbis = decimal.Round(gravado * porcentaje / 100m, 2);
        return (totalIngresado, itbis, totalIngresado + itbis);
    }
}
