using SQLite;

namespace PonceanoFacturador.Models;

public sealed class BitacoraAuditoria
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime FechaHora { get; set; } = DateTime.UtcNow;
    public string UsuarioId { get; set; } = string.Empty;
    public string Accion { get; set; } = string.Empty;
    public string TipoEntidad { get; set; } = string.Empty;
    public string IdEntidad { get; set; } = string.Empty;
    public string Detalle { get; set; } = string.Empty;
}
