using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class LicenciaPage : ContentPage
{
    private readonly LicenciaService _licencia;
    private readonly PayPalCheckoutService _checkout;
    private readonly ILogger<LicenciaPage> _logger;
    private bool _procesandoPago;

    public LicenciaPage(LicenciaService licencia, PayPalCheckoutService checkout, ILogger<LicenciaPage> logger)
    {
        InitializeComponent(); _licencia = licencia; _checkout = checkout; _logger = logger;
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

    private async void OnComprarMensualClicked(object? sender, EventArgs e) => await ComprarAsync("mensual", 35m, "30 días");
    private async void OnComprarAnualClicked(object? sender, EventArgs e) => await ComprarAsync("anual", 360m, "365 días");

    private async Task ComprarAsync(string plan, decimal importe, string duracion)
    {
        if (_procesandoPago) return;
        if (!await DisplayAlertAsync("Confirmar compra", $"Se abrirá PayPal Sandbox para pagar US${importe:N0} por {duracion}. No se iniciará un cobro real.", "Continuar", "Cancelar"))
            return;
        _procesandoPago = true;
        MensajeLabel.Text = "Preparando la orden de pago segura…";
        try
        {
            var orden = await _checkout.CrearOrdenAsync(plan);
            if (!await Browser.Default.OpenAsync(orden.UrlAprobacion, BrowserLaunchMode.SystemPreferred))
                throw new InvalidOperationException("No se pudo abrir la página segura de PayPal.");
            MensajeLabel.Text = "Completa el pago en PayPal Sandbox. Luego vuelve aquí y pulsa «YA COMPLETÉ EL PAGO» para actualizar la licencia.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo iniciar el checkout PayPal para el plan {Plan}.", plan);
            MensajeLabel.Text = ex is AuthException or InvalidOperationException or HttpRequestException or TaskCanceledException
                ? ex.Message
                : "No se pudo iniciar el pago. No se realizó ningún cobro; inténtalo más tarde.";
        }
        finally
        {
            _procesandoPago = false;
        }
    }

    private async void OnVerificarPagoClicked(object? sender, EventArgs e)
    {
        if (_procesandoPago) return;
        _procesandoPago = true;
        VerificarPagoButton.IsEnabled = false;
        try
        {
            var resultado = await _checkout.ActualizarLicenciaAsync();
            if (resultado.Estado == "Activo" && resultado.Vencimiento is DateTimeOffset vence && vence > DateTimeOffset.UtcNow)
                MensajeLabel.Text = $"✅ Licencia confirmada hasta {vence.ToLocalTime():dd/MM/yyyy}.";
            else
                MensajeLabel.Text = "PayPal aún no confirmó un pago para esta cuenta. Si acabas de pagar, espera unos segundos e inténtalo de nuevo.";
            await CargarAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo actualizar la licencia después del pago.");
            MensajeLabel.Text = ex is AuthException or HttpRequestException or TaskCanceledException or InvalidDataException
                ? ex.Message
                : "No se pudo consultar la licencia. Comprueba tu conexión e inténtalo de nuevo.";
        }
        finally
        {
            _procesandoPago = false;
            VerificarPagoButton.IsEnabled = true;
        }
    }

    private async void OnVolverClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
