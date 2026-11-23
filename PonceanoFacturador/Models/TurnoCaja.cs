using SQLite;

namespace PonceanoFacturador.Models;

public sealed class TurnoCaja
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Fecha { get; set; }
    public int NumeroTurno { get; set; }
    public DateTime HoraInicio { get; set; }
    public DateTime? HoraCierre { get; set; }
    public string UsuarioAbre { get; set; } = string.Empty;
    public string UsuarioCierra { get; set; } = string.Empty;
    public string Estado { get; set; } = "A";
    public string TotalSubtotal { get; set; } = "0.00";
    public string TotalItbis { get; set; } = "0.00";
    public string TotalGeneral { get; set; } = "0.00";
    public string TotalEfectivo { get; set; } = "0.00";
    public string TotalTarjeta { get; set; } = "0.00";
    public string TotalTransferencia { get; set; } = "0.00";
    public string TotalCredito { get; set; } = "0.00";
    public string TotalSistema { get; set; } = "0.00";
    public string TotalContado { get; set; } = "0.00";
    public string Diferencia { get; set; } = "0.00";
    public string MotivoDiferencia { get; set; } = string.Empty;
    public int CantidadFacturas { get; set; }
    public string Observaciones { get; set; } = string.Empty;
}
