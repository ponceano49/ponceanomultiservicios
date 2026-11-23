using SQLite;

namespace PonceanoFacturador.Models;

public sealed class NotaDebito
{
    [PrimaryKey]
    public Guid IdNota { get; set; }
    public string TipoNota { get; set; } = "D";
    public string NCF { get; set; } = string.Empty;
    public string NCFAfectado { get; set; } = string.Empty;
    public string RncComprador { get; set; } = string.Empty;
    public string NombreComprador { get; set; } = string.Empty;
    public DateTime FechaEmision { get; set; }
    public string Motivo { get; set; } = string.Empty;
    [Ignore]
    public decimal MontoFacturado { get; set; }
    [Column("MontoFacturado")]
    public string MontoFacturadoAlmacenado { get => MontoFacturado.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); set => MontoFacturado = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
    [Ignore]
    public decimal Itbis { get; set; }
    [Column("Itbis")]
    public string ItbisAlmacenado { get => Itbis.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); set => Itbis = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
    [Ignore]
    public decimal TotalMonto { get; set; }
    [Column("TotalMonto")]
    public string TotalMontoAlmacenado { get => TotalMonto.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); set => TotalMonto = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
    public Guid IdFacturaOrigen { get; set; }
    public string IdTurno { get; set; } = string.Empty;
    public string Estado { get; set; } = "A";
    public string IdUsuario { get; set; } = string.Empty;
    public DateTime FechaRegistro { get; set; } = DateTime.UtcNow;
    public string MotivoAnulacion { get; set; } = string.Empty;
}
