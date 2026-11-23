namespace PonceanoFacturador.Models;

public sealed class ResumenMensual
{
    public int Cotizaciones { get; set; }
    public int Facturas { get; set; }
    public decimal TotalVentas { get; set; }
    public decimal TotalItbis { get; set; }
}
