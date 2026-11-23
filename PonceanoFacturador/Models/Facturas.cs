using System.Text.Json;
using SQLite;

namespace PonceanoFacturador.Models;

public class Factura
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public string NCF { get; set; } = string.Empty;
    public string TipoNCF { get; set; } = "B01";
    public string TipoOperacion { get; set; } = "Venta";
    public string TurnoId { get; set; } = string.Empty;
    public Guid ClienteId { get; set; }
    public string ClienteNombre { get; set; } = string.Empty;
    public string ClienteRnc { get; set; } = string.Empty;
    public DateTime Fecha { get; set; }
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
    [Ignore]
    public decimal ItbisOriginal { get; set; }
    [Column("ItbisOriginal")]
    public string ItbisOriginalAlmacenado { get => ItbisOriginal.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); set => ItbisOriginal = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
    [Ignore]
    public decimal MontoOriginal { get; set; }
    [Column("MontoOriginal")]
    public string MontoOriginalAlmacenado { get => MontoOriginal.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); set => MontoOriginal = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
    public string MonedaOriginal { get; set; } = "DOP";
    [Ignore]
    public decimal TasaCambio { get; set; }
    [Column("TasaCambio")]
    public string TasaCambioAlmacenado { get => TasaCambio.ToString("F4", System.Globalization.CultureInfo.InvariantCulture); set => TasaCambio = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
    public string RutaArchivoOrigen { get; set; } = string.Empty;
    public string BorradorFiscalJson { get; set; } = string.Empty;
    public bool ClasificacionOcrSupuesta { get; set; }
    public bool Anulada { get; set; }
    public DateTime? FechaAnulacion { get; set; }
    public string Estado { get; set; } = "Emitida";
    public string UsuarioId { get; set; } = string.Empty;
    public string InstruccionPago { get; set; } = string.Empty;
    public string EstadoPago { get; set; } = "Pendiente";
    public string MetodoPago { get; set; } = string.Empty;
    public bool Enviada { get; set; }
    public DateTime? VenceFecha { get; set; }
    public bool VencimientoEliminado { get; set; }
    public string Nota { get; set; } = string.Empty;
    public string FirmaEmpresaRuta { get; set; } = string.Empty;
    public string FirmaClienteRuta { get; set; } = string.Empty;
    public bool SolicitarFirmaCliente { get; set; }
    [Ignore]
    public string VenceTexto => VenceFecha is DateTime fecha ? $"Vence: {fecha.ToLocalTime():dd/MM/yyyy}" : string.Empty;
    [Ignore]
    public string EstadoCorto => Anulada ? "Anulada" : Estado == "Guardada" ? "Guardado" : EstadoPago == "Pagada" ? "Pagado" : Enviada ? "Enviado" : VenceFecha is DateTime vence && vence.ToLocalTime().Date < DateTime.Today ? "Atrasado" : "Sin pagar";
    [Ignore]
    public Color EstadoColor => EstadoCorto switch
    {
        "Guardado" => Color.FromArgb("#BFC3C7"),
        "Atrasado" => Color.FromArgb("#F44247"),
        "Pagado" => Color.FromArgb("#36AD52"),
        "Enviado" => Color.FromArgb("#4C9DD9"),
        "Anulada" => Color.FromArgb("#777777"),
        _ => Color.FromArgb("#D8D8D8")
    };
    [Ignore]
    public Color ColorFondoFila { get; set; } = Color.FromArgb("#FFFFFF");
    [Ignore]
    public string ImporteVisual => $"RD${Total:N2}";
    [Ignore]
    public string EstadoVisual => EstadoCorto;
    [Ignore]
    public decimal RecargoPorcentaje { get; set; }
    [Column("RecargoPorcentaje")]
    public string RecargoPorcentajeAlmacenado { get => RecargoPorcentaje.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); set => RecargoPorcentaje = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
    [Ignore]
    public decimal RecargoMonto { get; set; }
    [Column("RecargoMonto")]
    public string RecargoMontoAlmacenado { get => RecargoMonto.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); set => RecargoMonto = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
}
