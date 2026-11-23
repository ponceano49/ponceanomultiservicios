using SQLite;

namespace PonceanoFacturador.Models;

public sealed class DetallePedidoCompra
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PedidoId { get; set; } = string.Empty;
    public int ArticuloId { get; set; }
    public string DescripcionArticulo { get; set; } = string.Empty;
    public int CantidadPedida { get; set; }
    public int CantidadRecibida { get; set; }
    public string CostoUnitario { get; set; } = "0.00";
}
