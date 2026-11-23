using System.Text.Json;
using SQLite;

namespace PonceanoFacturador.Models;

public class NotaCredito
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public string NCF { get; set; } = string.Empty;
    public DateTime Fecha { get; set; }
    public Guid ClienteId { get; set; }
    public string ClienteNombre { get; set; } = string.Empty;
    public string ClienteRnc { get; set; } = string.Empty;
    public Guid? FacturaRelacionadaId { get; set; }
    public string Motivo { get; set; } = string.Empty;
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
    public bool Anulada { get; set; }
    public DateTime FechaCreacion { get; set; }
    public string UsuarioId { get; set; } = string.Empty;
    public string MotivoAnulacion { get; set; } = string.Empty;
    [Ignore]
    public bool EsBorrador => string.IsNullOrEmpty(NCF);
    [Ignore]
    public string Estado => Anulada ? "Anulada" : EsBorrador ? "Borrador" : "Emitida";
}
