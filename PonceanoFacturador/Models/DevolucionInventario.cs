using SQLite;

namespace PonceanoFacturador.Models;

public sealed class DevolucionInventario
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UsuarioId { get; set; } = string.Empty;
    public DateTime Fecha { get; set; } = DateTime.UtcNow;
    public string Direccion { get; set; } = string.Empty;
    public string Contraparte { get; set; } = string.Empty;
    public int ArticuloId { get; set; }
    public string Articulo { get; set; } = string.Empty;
    public int Cantidad { get; set; }
    public string Motivo { get; set; } = string.Empty;
}
