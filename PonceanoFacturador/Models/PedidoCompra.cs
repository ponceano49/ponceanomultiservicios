using SQLite;

namespace PonceanoFacturador.Models;

public sealed class PedidoCompra
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Fecha { get; set; } = DateTime.UtcNow;
    public string ProveedorNombre { get; set; } = string.Empty;
    public string ProveedorRnc { get; set; } = string.Empty;
    public string NcfProveedor { get; set; } = string.Empty;
    public string Estado { get; set; } = "Pendiente";
    public string UsuarioId { get; set; } = string.Empty;
    public DateTime? FechaRecepcion { get; set; }
}
