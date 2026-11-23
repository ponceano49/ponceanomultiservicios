using SQLite;

namespace PonceanoFacturador.Models;

public sealed class HistorialCambioProducto
{
    [PrimaryKey]
    public string IdCambio { get; set; } = Guid.NewGuid().ToString("N");
    public int IdProducto { get; set; }
    public DateTime FechaHora { get; set; } = DateTime.UtcNow;
    public string IdUsuario { get; set; } = string.Empty;
    public string NombreUsuario { get; set; } = string.Empty;
    public string CampoModificado { get; set; } = string.Empty;
    public string ValorAnterior { get; set; } = string.Empty;
    public string ValorNuevo { get; set; } = string.Empty;
    public string Motivo { get; set; } = string.Empty;
}
