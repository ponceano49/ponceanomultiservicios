using SQLite;

namespace PonceanoFacturador.Models;

public sealed class HistorialExportacionDGII
{
    [PrimaryKey]
    public string IdExportacion { get; set; } = Guid.NewGuid().ToString("N");
    public string TipoReporte { get; set; } = string.Empty;
    public int PeriodoMes { get; set; }
    public int PeriodoAnio { get; set; }
    public DateTime FechaHora { get; set; } = DateTime.UtcNow;
    public string IdUsuario { get; set; } = string.Empty;
    public string NombreArchivo { get; set; } = string.Empty;
    public string RutaGuardado { get; set; } = string.Empty;
    public int CantidadRegistros { get; set; }
    public string Estado { get; set; } = "Borrador interno";
    public string ObservacionesDGII { get; set; } = string.Empty;
}
