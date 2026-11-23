using SQLite;

namespace PonceanoFacturador.Models;

public class ConfiguracionUsuario
{
    [PrimaryKey]
    public string UsuarioId { get; set; } = string.Empty;
    public string LicenciaTipo { get; set; } = "Gratuito";
    public DateTime? LicenciaVencimiento { get; set; }
    public DateTime? FechaInicioPruebaUtc { get; set; }
    public DateTime? FechaVencimientoPruebaUtc { get; set; }
    public string IdentificadorHardware { get; set; } = string.Empty;
    public int CotizacionesEsteMes { get; set; }
    public int FacturasEsteMes { get; set; }
    public DateTime UltimoMesConteo { get; set; }
    public int FacturasEsteAnio { get; set; }
    public DateTime UltimoAnioConteo { get; set; }
}
