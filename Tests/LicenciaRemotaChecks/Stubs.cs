namespace PonceanoFacturador.Models
{
    public class UsuarioAutenticado
    {
        public string IdFirebase { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Correo { get; set; } = "";
        public DateTime UltimoAcceso { get; set; }
    }
    public class ConfiguracionUsuario
    {
        public string UsuarioId { get; set; } = "";
        public string LicenciaTipo { get; set; } = "Gratuito";
        public DateTime? LicenciaVencimiento { get; set; }
        public DateTime? FechaInicioPruebaUtc { get; set; }
        public DateTime? FechaVencimientoPruebaUtc { get; set; }
        public string IdentificadorHardware { get; set; } = "";
    }
}
namespace PonceanoFacturador.Services
{
    public static class FirebaseOptions { public static string WebApiKey => "test"; }
    public class DatabaseService
    {
        public PonceanoFacturador.Models.ConfiguracionUsuario State { get; set; } = new();
        public (string Id, DateTime Vencimiento, bool Priorizar)? Remota { get; private set; }
        public int TrialWrites { get; private set; }
        public Task SaveUserAsync(PonceanoFacturador.Models.UsuarioAutenticado user) => Task.CompletedTask;
        public Task<PonceanoFacturador.Models.ConfiguracionUsuario> ObtenerConfiguracionLicenciaSinActualizarAsync(string id) => Task.FromResult(State);
        public void LimpiarLicenciaRemota() => Remota = null;
        public void EstablecerLicenciaRemota(string id, DateTime vencimiento, bool priorizar, Func<bool> sesionActiva) => Remota = sesionActiva() ? (id, vencimiento, priorizar) : null;
        public Task ActivarPruebaGratuitaAsync(string id, string hardware, DateTime inicio, DateTime fin) { TrialWrites++; return Task.CompletedTask; }
#if DEBUG
        public Task ReiniciarPruebaDebugAsync(string id) => Task.CompletedTask;
#endif
    }
}
public enum NetworkAccess { Internet, None }
public sealed class Connectivity
{
    public static Connectivity Current { get; } = new();
    public NetworkAccess NetworkAccess { get; set; } = NetworkAccess.Internet;
}
public sealed class SecureStorage
{
    public static SecureStorage Default { get; } = new();
    private readonly Dictionary<string, string> _values = new();
    public Task<string?> GetAsync(string key) => Task.FromResult(_values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
    public bool Remove(string key) => _values.Remove(key);
}
