namespace PonceanoFacturador.Models;

public class ItemFactura
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int? ArticuloId { get; set; }
    public string Descripcion { get; set; } = string.Empty;
    public int Cantidad { get; set; } = 1;
    public decimal PrecioUnitario { get; set; }
    public bool SujetoImpuesto { get; set; } = true;
    public decimal Subtotal => Cantidad * PrecioUnitario;
}
