using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class LicenciaPage : ContentPage
{
    private readonly LicenciaService _licencia;
    private readonly ILogger<LicenciaPage> _logger;

    public LicenciaPage(LicenciaService licencia, ILogger<LicenciaPage> logger)
    {
        InitializeComponent(); _licencia = licencia; _logger = logger;
    }

    protected override async void OnAppearing() { base.OnAppearing(); await CargarAsync(); }

    private async Task CargarAsync()
    {
        try
        {
            var estado = await _licencia.ObtenerEstadoLicenciaAsync();
            var activa = LicenciaService.LicenciaActiva(estado);
            var esPrueba = estado.FechaInicioPruebaUtc.HasValue && estado.FechaVencimientoPruebaUtc.HasValue;
            var consumo = await _licencia.ObtenerConsumoLiteFacturasAsync();
            TipoLabel.Text = esPrueba ? "🆓 Prueba gratuita de 30 días" : activa && estado.LicenciaTipo == "Mensual"
                ? LicenciaPruebaService.MensajeLicenciaCompletaActiva
                : activa ? "⭐ Plan PRO activo" : "🆓 Estás en el Plan LITE (Gratis)";
            SuscripcionButton.Text = activa && !esPrueba ? "ADMINISTRAR SUSCRIPCIÓN" : "SUSCRIBIRSE AHORA";
            VencimientoLabel.Text = activa && estado.LicenciaVencimiento is DateTime fecha
                ? (esPrueba ? $"Prueba activa; vence el: {fecha.ToLocalTime():dd/MM/yyyy}" : $"Vence el: {fecha.ToLocalTime():dd/MM/yyyy}")
                : "Todos los dispositivos incluidos";
            if (activa && estado.LicenciaVencimiento is DateTime vence)
            {
                var dias = (vence.ToLocalTime().Date - DateTime.Today).Days;
                EstadoLabel.Text = estado.LicenciaTipo == "Mensual"
                    ? LicenciaPruebaService.MensajeLicenciaCompletaActiva
                    : dias <= 7 ? $"⚠️ Activo — vence en {dias} días" : "✅ Activo";
                if (dias <= 7) MensajeLabel.Text = $"⚠️ Tu licencia vence en {dias} días. Renueva aquí.";
            }
            else
            {
                var vencida = estado.LicenciaVencimiento is DateTime fechaVencimiento && fechaVencimiento <= DateTime.UtcNow;
                EstadoLabel.Text = esPrueba && vencida
                    ? "🔒 Período de prueba finalizado; el acceso está bloqueado. Adquiere una licencia completa."
                    : vencida ? "🔒 Licencia vencida; se aplican los límites gratuitos" : "✅ Plan gratuito activo";
            }
            CotizacionesLabel.Text = esPrueba && !activa
                ? "Acceso bloqueado hasta activar una licencia completa."
                : activa ? "Clientes y cotizaciones ilimitados."
                : $"Facturas LITE: {consumo.EsteAnio}/40 este año; {consumo.Ultimos30Dias}/5 en los últimos 30 días.";
            FacturasLabel.Text = esPrueba && !activa
                ? "Acceso bloqueado."
                : activa ? "Facturas ilimitadas." : "El límite se aplica a facturas emitidas; los borradores no consumen cuota.";
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo consultar la licencia."); MensajeLabel.Text = ex.Message; }
    }

    private async void OnSuscripcionClicked(object? sender, EventArgs e)
    {
        try
        {
            var uri = new Uri("https://ponceanomultiservicios.com/suscripcion");
            if (!await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred))
                throw new InvalidOperationException("No se pudo abrir el sitio de suscripción.");
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir el sitio de suscripción."); MensajeLabel.Text = "No se pudo abrir la página. Inténtalo desde el navegador: ponceanomultiservicios.com/suscripcion"; }
    }
    private async void OnVolverClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
