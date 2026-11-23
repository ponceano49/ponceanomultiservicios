using SQLite;

namespace PonceanoFacturador.Models;

public sealed class PeriodoFiscal
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    public string UsuarioId { get; set; } = string.Empty;
    public int Anio { get; set; }
    public int Mes { get; set; }
    public DateTime FechaInicio { get; set; }
    public DateTime? FechaCierre { get; set; }
    public string Estado { get; set; } = "A";
    public string UsuarioCierra { get; set; } = string.Empty;
    public DateTime? FechaHoraCierre { get; set; }
    public string UsuarioReabre { get; set; } = string.Empty;
    public DateTime? FechaHoraReapertura { get; set; }
    public string MotivoReapertura { get; set; } = string.Empty;
    public string Observaciones { get; set; } = string.Empty;
}
