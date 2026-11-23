using System.Net.Mail;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class RegistroPage : ContentPage
{
    private readonly AuthService _auth;
    private readonly ILogger<RegistroPage> _logger;
    private bool _busy;
    private CancellationTokenSource? _verification;
    private SemaphoreSlim? _reintentoVerificacion;

    public RegistroPage(AuthService auth, ILogger<RegistroPage> logger)
    {
        InitializeComponent();
        _auth = auth;
        _logger = logger;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _verification?.Cancel();
    }

    private void UpdateButton()
    {
        CrearButton.IsEnabled = !_busy && TerminosCheck.IsChecked &&
            !string.IsNullOrWhiteSpace(NombreEntry.Text) && !string.IsNullOrWhiteSpace(CorreoEntry.Text) &&
            !string.IsNullOrEmpty(ContrasenaEntry.Text) && !string.IsNullOrEmpty(ConfirmarEntry.Text);
        TerminosButton.IsEnabled = !_busy;
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e) => UpdateButton();
    private void OnTermsChanged(object? sender, CheckedChangedEventArgs e) => UpdateButton();

    private async void OnTermsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new TerminosPage());
    }

    private async void OnRegisterClicked(object? sender, EventArgs e)
    {
        if (!TerminosCheck.IsChecked)
        {
            MensajeLabel.Text = "Debes aceptar los Términos y Condiciones.";
            return;
        }
        if (!MailAddress.TryCreate(CorreoEntry.Text?.Trim(), out _))
        {
            MensajeLabel.Text = "Introduce un correo electrónico válido.";
            return;
        }
        if (ContrasenaEntry.Text.Length < 6 || ContrasenaEntry.Text != ConfirmarEntry.Text)
        {
            MensajeLabel.Text = "La contraseña debe tener al menos 6 caracteres y coincidir con la confirmación.";
            return;
        }

        _busy = true;
        UpdateButton();
        Loading.IsVisible = Loading.IsRunning = true;
        _verification = new CancellationTokenSource();
        try
        {
var correo = CorreoEntry.Text.Trim();
var pending = await _auth.RegisterAsync(NombreEntry.Text.Trim(), correo, ContrasenaEntry.Text);
_verification.Token.ThrowIfCancellationRequested();
if (AuthService.EsAccesoPruebaDebug(correo))
{
    await _auth.SignInAsync(correo, ContrasenaEntry.Text, false, _verification.Token);
    ContrasenaEntry.Text = ConfirmarEntry.Text = string.Empty;
}
else
{
    ContrasenaEntry.Text = ConfirmarEntry.Text = string.Empty;
    MensajeLabel.TextColor = Colors.DarkGreen;
    MensajeLabel.Text = "Confirma el enlace que te llegó a tu correo y presiona Reintentar. También comprobaremos automáticamente.";
    ReintentarVerificacionButton.IsVisible = CancelarVerificacionButton.IsVisible = true;
    _reintentoVerificacion = new SemaphoreSlim(0, 1);
    await _auth.WaitForVerificationAsync(pending, _verification.Token, _reintentoVerificacion);
    ReintentarVerificacionButton.IsVisible = CancelarVerificacionButton.IsVisible = false;
}
await MainThread.InvokeOnMainThreadAsync(() => ((App)Application.Current!).ShowAuthenticatedAppAsync());
        }
        catch (OperationCanceledException) when (_verification?.IsCancellationRequested == true)
        {
            MensajeLabel.Text = "Registro o verificación cancelados. Puedes intentar iniciar sesión nuevamente.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo completar el registro o la verificación.");
            MensajeLabel.TextColor = Colors.DarkRed;
            MensajeLabel.Text = AuthService.UserMessage(ex);
        }
        finally
        {
            ReintentarVerificacionButton.IsVisible = CancelarVerificacionButton.IsVisible = false;
            _reintentoVerificacion?.Dispose();
            _reintentoVerificacion = null;
            _verification?.Dispose();
            _verification = null;
            _busy = false;
            Loading.IsVisible = Loading.IsRunning = false;
            UpdateButton();
        }
    }

    private void OnReintentarVerificacionClicked(object? sender, EventArgs e)
    {
        if (_reintentoVerificacion is { CurrentCount: 0 } reintento) reintento.Release();
    }

    private void OnCancelarVerificacionClicked(object? sender, EventArgs e) => _verification?.Cancel();

    private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();
}
