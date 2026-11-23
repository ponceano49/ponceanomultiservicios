using System.Text.Json;
using SQLite;

namespace PonceanoFacturador.Models;

public class Cotizacion
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public Guid ClienteId { get; set; }
    public string ClienteNombre { get; set; } = string.Empty;
    public string UsuarioId { get; set; } = string.Empty;
    public DateTime Fecha { get; set; }
    public DateTime ValidezHasta { get; set; }
    public string ItemsJson { get; set; } = "[]";
    [Ignore]
    public List<ItemFactura> Items
    {
        get => JsonSerializer.Deserialize<List<ItemFactura>>(ItemsJson) ?? new();
        set => ItemsJson = JsonSerializer.Serialize(value ?? new());
    }
    [Ignore]
    public decimal Subtotal { get; set; }
    [Column("Subtotal")]
    public string SubtotalAlmacenado { get => Subtotal.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); set => Subtotal = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
    [Ignore]
    public decimal Itbis { get; set; }
    [Column("Itbis")]
    public string ItbisAlmacenado { get => Itbis.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); set => Itbis = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
    [Ignore]
    public decimal Total { get; set; }
    [Column("Total")]
    public string TotalAlmacenado { get => Total.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); set => Total = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
    public bool ConvertidaAFactura { get; set; }
    public DateTime? FechaConversion { get; set; }
    public bool Enviada { get; set; }
    public bool Pagada { get; set; }
    public string Nota { get; set; } = string.Empty;
    public string FirmaEmpresaRuta { get; set; } = string.Empty;
    public string FirmaClienteRuta { get; set; } = string.Empty;
    [Ignore]
    public string EstadoCorto => ConvertidaAFactura ? "Convertida" : Pagada ? "Pagado" : Enviada ? "Enviado" : ValidezHasta.Date < DateTime.Today ? "Atrasado" : "Guardado";
    [Ignore]
    public Color EstadoColor => EstadoCorto switch
    {
        "Enviado" => Color.FromArgb("#258AC7"),
        "Atrasado" => Color.FromArgb("#F44247"),
        "Pagado" => Color.FromArgb("#36AD52"),
        "Convertida" => Color.FromArgb("#777777"),
        _ => Color.FromArgb("#BFC3C7")
    };
    [Ignore]
    public Color ColorFondoFila { get; set; } = Color.FromArgb("#FFFFFF");
    [Ignore]
    public string NumeroVisual => Id == Guid.Empty ? "" : Id.ToString("N")[..4].ToUpperInvariant();
    [Ignore]
    public string ImporteVisual => $"RD${Total:N2}";
    [Ignore]
    public string EstadoVisual => EstadoCorto;
}
