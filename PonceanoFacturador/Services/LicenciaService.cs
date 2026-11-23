using PonceanoFacturador.Models;

namespace PonceanoFacturador.Services;

public sealed class LicenciaService
{
    public const int LimiteMensual = 5;
    private readonly DatabaseService _database;
    private readonly AuthService _auth;

    public LicenciaService(DatabaseService database, AuthService auth)
    {
        _database = database;
        _auth = auth;
    }

    private string UsuarioId => _auth.CurrentUser?.IdFirebase
        ?? throw new InvalidOperationException("Debes iniciar sesión para consultar la licencia.");

    public Task<ConfiguracionUsuario> ObtenerEstadoLicenciaAsync() =>
        _database.ObtenerConfiguracionUsuarioAsync(UsuarioId);

    public async Task<bool> PuedeCrearCotizacionAsync()
    {
        var estado = await ObtenerEstadoLicenciaAsync();
        return LicenciaActiva(estado) || estado.CotizacionesEsteMes < LimiteMensual;
    }

    public Task<bool> PuedeCrearFacturaAsync() => _database.PuedeCrearFacturaAsync(UsuarioId);

    public Task<(int Ultimos30Dias, int EsteAnio)> ObtenerConsumoLiteFacturasAsync() =>
        _database.ObtenerConsumoLiteFacturasAsync(UsuarioId);

    public Task IncrementarContadorCotizacionAsync() => IncrementarAsync(true);
    public Task IncrementarContadorFacturaAsync() => IncrementarAsync(false);

    private Task IncrementarAsync(bool cotizacion) =>
        _database.IncrementarContadorDocumentoAsync(UsuarioId, cotizacion);

    public Task ActivarLicenciaAsync(string tipo)
    {
        if (tipo is not ("Mensual" or "Trimestral" or "Semestral" or "Anual"))
            throw new ArgumentOutOfRangeException(nameof(tipo), "El plan debe ser Mensual, Trimestral, Semestral o Anual.");
        return _database.ActivarLicenciaAsync(UsuarioId, tipo);
    }

    public Task ReiniciarConteoSiCambioMesAsync() => ObtenerEstadoLicenciaAsync();

    public async Task<int> QuedanCotizacionesAsync()
    {
        var estado = await ObtenerEstadoLicenciaAsync();
        return LicenciaActiva(estado) ? int.MaxValue : Math.Max(0, LimiteMensual - estado.CotizacionesEsteMes);
    }

    public async Task<int> QuedanFacturasAsync()
    {
        var estado = await ObtenerEstadoLicenciaAsync();
        if (LicenciaActiva(estado)) return int.MaxValue;
        var consumo = await ObtenerConsumoLiteFacturasAsync();
        return Math.Max(0, Math.Min(5 - consumo.Ultimos30Dias, 40 - consumo.EsteAnio));
    }

    public static bool LicenciaActiva(ConfiguracionUsuario estado) =>
        estado.LicenciaTipo != "Gratuito" && estado.LicenciaVencimiento is DateTime vencimiento && vencimiento > DateTime.UtcNow;
}
