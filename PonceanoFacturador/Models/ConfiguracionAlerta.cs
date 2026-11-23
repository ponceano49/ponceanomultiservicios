using SQLite;

namespace PonceanoFacturador.Models;

public sealed class ConfiguracionAlerta
{
    [PrimaryKey, AutoIncrement]
    public int IdAlerta { get; set; }
    public string UsuarioId { get; set; } = string.Empty;
    public string TipoAlerta { get; set; } = string.Empty;
    public string Condicion { get; set; } = string.Empty;
    public string FechaAviso { get; set; } = string.Empty;
    public string HoraAviso { get; set; } = "09:00";
    [Ignore]
    public decimal Umbral { get; set; }
    [Column("Umbral")]
    public string UmbralAlmacenado
    {
        get => Umbral.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        set => Umbral = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
    public bool Activa { get; set; } = true;
    public string MensajePersonalizado { get; set; } = string.Empty;
}
