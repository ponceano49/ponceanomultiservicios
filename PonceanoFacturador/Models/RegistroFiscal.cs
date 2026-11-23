using SQLite;

namespace PonceanoFacturador.Models;

public sealed class RegistroFiscal
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UsuarioId { get; set; } = string.Empty;
    public string TipoOperacion { get; set; } = string.Empty;
    public string FacturaId { get; set; } = string.Empty;
    public string PedidoId { get; set; } = string.Empty;
    public string TurnoId { get; set; } = string.Empty;
    public DateTime FechaComprobante { get; set; }
    public string RncContraparte { get; set; } = string.Empty;
    public string RazonSocialContraparte { get; set; } = string.Empty;
    public string Ncf { get; set; } = string.Empty;
    public string NcfModificado { get; set; } = string.Empty;
    public string TipoBienServicio { get; set; } = string.Empty;
    public string TipoIngresoGasto { get; set; } = string.Empty;
    public string MontoGravadoBienes { get; set; } = "0.00";
    public string MontoGravadoServicios { get; set; } = "0.00";
    public string MontoGravadoTotal { get; set; } = "0.00";
    public string MontoExento { get; set; } = "0.00";
    public string TotalFacturado { get; set; } = "0.00";
    public string ItbisFacturado { get; set; } = "0.00";
    public string ItbisRetenido { get; set; } = "0.00";
    public string IsrRetenido { get; set; } = "0.00";
    public string OtrosImpuestos { get; set; } = "0.00";
    public string PropinaLegal { get; set; } = "0.00";
    public string CondicionPago { get; set; } = string.Empty;
    public string MetodoPago { get; set; } = string.Empty;
    public string EstadoRevision { get; set; } = "Pendiente de revisión";
    public string EstadoValidacionDgii { get; set; } = "Pendiente";
    public string FuenteJson { get; set; } = string.Empty;
    public DateTime FechaRegistro { get; set; } = DateTime.UtcNow;
    [Ignore]
    public string ResumenVisual => $"{FechaComprobante.ToLocalTime():dd/MM/yyyy} · {Ncf} · RNC/Cédula: {RncContraparte} · RD$ {TotalFacturado} · {EstadoRevision}";
}
