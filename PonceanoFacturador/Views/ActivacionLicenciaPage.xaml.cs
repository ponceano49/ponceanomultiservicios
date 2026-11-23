using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class ActivacionLicenciaPage : ContentPage
{
    private readonly AuthService _auth;
    private readonly LicenciaPruebaService _licenciaPrueba;
    private readonly RelojNtpService _relojNtp;
    private readonly ILogger<ActivacionLicenciaPage> _logger;
    private bool _ocupado;

    public ActivacionLicenciaPage(
        AuthService auth,
        LicenciaPruebaService licenciaPrueba,
        RelojNtpService relojNtp,
        ILogger<ActivacionLicenciaPage> logger)
    {
        InitializeComponent();
        _auth = auth;
        _licenciaPrueba = licenciaPrueba;
        _relojNtp = relojNtp;
        _logger = logger;
#if DEBUG
        var reiniciarPrueba = new Button { Text = "DESARROLLADOR: REINICIAR PRUEBA LOCAL", BackgroundColor = Color.FromArgb("#713E00"), TextColor = Colors.White };
        reiniciarPrueba.Clicked += OnReiniciarPruebaDebugClicked;
        ContenidoLicencia.Children.Insert(ContenidoLicencia.Children.IndexOf(ComprarButton), reiniciarPrueba);
#endif
    }

    public void Configurar(ResultadoValidacionLicenciaInicial resultado)
    {
        if (resultado.RequiereActivacionPrueba)
        {
            TituloLabel.Text = "ACTIVAR PRUEBA GRATUITA DE 30 DÍAS";
            MensajeLabel.Text = "Activa una sola prueba gratuita. La fecha de inicio se obtiene de servidores NTP y la prueba queda vinculada a esta cuenta y a este dispositivo. Después de 30 días necesitarás una licencia completa.";
            EstadoLabel.Text = resultado.Mensaje;
            ActivarButton.IsVisible = true;
            ComprarButton.IsVisible = true;
            return;
        }

        TituloLabel.Text = "ACCESO A PONCEANO FACTURADOR BLOQUEADO";
        MensajeLabel.Text = resultado.Mensaje;
        EstadoLabel.Text = "Adquiere o renueva una licencia completa y vuelve a validar. No se modificaron los datos de la cuenta.";
        ActivarButton.IsVisible = false;
        ComprarButton.IsVisible = true;
    }

    private async void OnActivarClicked(object? sender, EventArgs e)
    {
        if (_ocupado) return;
        _ocupado = true;
        ActivarButton.IsEnabled = false;
        EstadoLabel.Text = "Consultando hora segura y activando la prueba…";
        var activada = false;
        try
        {
            var usuarioId = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para activar la prueba.");
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
                throw new InvalidOperationException(ValidacionLicenciaNtpService.MensajeSinConexion);
            var horaNtp = await _relojNtp.ObtenerHoraUtcAsync();
            var vencimiento = await _licenciaPrueba.ActivarAsync(usuarioId, horaNtp);
            activada = true;
            await DisplayAlertAsync("Prueba activada", $"✅ Tu prueba gratuita de 30 días está activa hasta {vencimiento.ToLocalTime():dd/MM/yyyy HH:mm}. Todas las funciones quedan disponibles durante el período.", "Continuar");
            await ((App)Application.Current!).ShowAuthenticatedAppAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo completar la activación de la prueba o abrir la aplicación.");
#if ANDROID && DEBUG
            Android.Util.Log.Error("PonceanoLicencia", ex.ToString());
#endif
            EstadoLabel.Text = activada ? "La prueba está activa, pero no se pudo abrir la aplicación. Vuelve a validar la licencia." :
                ex is InvalidOperationException ? ex.Message : "No se pudo activar la prueba. Comprueba la conexión e inténtalo nuevamente.";
        }
        finally
        {
            _ocupado = false;
            ActivarButton.IsEnabled = true;
        }
    }

    private async void OnComprarClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!await Browser.Default.OpenAsync(new Uri("https://ponceanomultiservicios.com/suscripcion"), BrowserLaunchMode.SystemPreferred))
                throw new InvalidOperationException("No se pudo abrir la página de suscripción.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir el sitio de suscripción desde la pantalla de licencia.");
            EstadoLabel.Text = "No se pudo abrir la página. Inténtalo desde el navegador: ponceanomultiservicios.com/suscripcion";
        }
    }

    private async void OnReintentarClicked(object? sender, EventArgs e) =>
        await ((App)Application.Current!).ShowAuthenticatedAppAsync();

#if DEBUG
    private async void OnReiniciarPruebaDebugClicked(object? sender, EventArgs e)
    {
        if (_ocupado || sender is not Button boton) return;
        _ocupado = true;
        boton.IsEnabled = false;
        ActivarButton.IsEnabled = false;
        ReintentarButton.IsEnabled = false;
        try
        {
            var confirmar = await DisplayAlertAsync("Solo desarrollo", "Se eliminará el bloqueo de prueba de este dispositivo, incluso si pertenece a otra cuenta, y se reiniciará la prueba local de la cuenta actual. No se borrarán documentos ni licencias pagadas. ¿Continuar?", "REINICIAR", "CANCELAR");
            if (!confirmar) return;
            var usuarioId = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión antes de reiniciar la prueba.");
            await _licenciaPrueba.ReiniciarPruebaDebugAsync(usuarioId);
            await DisplayAlertAsync("Prueba reiniciada", "La prueba local se reinició. Vuelve a validarla y activa la prueba de 30 días con conexión a internet.", "Continuar");
            await ((App)Application.Current!).ShowAuthenticatedAppAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo reiniciar la prueba local de desarrollo.");
            EstadoLabel.Text = ex is InvalidOperationException ? ex.Message : "No se pudo reiniciar la prueba local. Revisa los datos e inténtalo nuevamente.";
        }
        finally
        {
            _ocupado = false;
            boton.IsEnabled = true;
            ActivarButton.IsEnabled = true;
            ReintentarButton.IsEnabled = true;
        }
    }
#endif
}
