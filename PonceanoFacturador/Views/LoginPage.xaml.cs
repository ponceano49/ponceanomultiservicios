using System.Net.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class LoginPage : ContentPage
{
    private readonly AuthService _auth;
    private readonly IServiceProvider _services;
    private readonly ILogger<LoginPage> _logger;
    private bool _busy;
    private bool _loginRequested;
    private bool _restoreAttempted;
    private CancellationTokenSource? _restoreCancellation;

    public LoginPage(AuthService auth, IServiceProvider services, ILogger<LoginPage> logger)
    {
        InitializeComponent();
        _auth = auth;
        _services = services;
        _logger = logger;
        UpdateButton();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_restoreAttempted) return;
        _restoreAttempted = true;
        _restoreCancellation = new CancellationTokenSource();
        _ = RestoreSessionWithTimeoutAsync(_restoreCancellation);
    }

    protected override void OnDisappearing()
    {
        _restoreCancellation?.Cancel();
        base.OnDisappearing();
    }

    private async Task RestoreSessionWithTimeoutAsync(CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var restoreTask = Task.Run(() => _auth.RestoreAsync(token), token);
        try
        {
            var restored = await restoreTask.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (restored && !_loginRequested && !token.IsCancellationRequested)
                await ((App)Application.Current!).ShowAuthenticatedAppAsync();
        }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            _logger.LogInformation("La restauración automática de sesión excedió cinco segundos y se canceló.");
            _ = ObserveRestoreCompletionAsync(restoreTask);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = ObserveRestoreCompletionAsync(restoreTask);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo restaurar la sesión guardada; el inicio manual sigue disponible.");
        }
        finally
        {
            if (ReferenceEquals(_restoreCancellation, cancellation)) _restoreCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task ObserveRestoreCompletionAsync(Task<bool> restoreTask)
    {
        try { await restoreTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "La restauración de sesión cancelada terminó con un error."); }
    }

    private async Task ObservarInicioCanceladoAsync(Task tarea)
    {
        try { await tarea; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "La autenticación cancelada terminó con un error."); }
    }

    private void UpdateButton()
    {
        IngresarButton.IsEnabled = !_busy && !_loginRequested;
        RecuperarButton.IsEnabled = RegistroButton.IsEnabled = !_busy && !_loginRequested;
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e) => UpdateButton();

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateButton();
        Loading.IsVisible = Loading.IsRunning = true;
        MensajeLabel.Text = string.Empty;
        try { await action(); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ConexionAuthException)
        {
            _logger.LogWarning(ex, "No se pudo validar la cuenta por conexión o tiempo de espera.");
            MensajeLabel.Text = "⚠️ No se pudo validar tu cuenta. Revisa tu conexión a internet e inténtalo de nuevo. La validación de licencia requiere conexión.";
            _busy = false;
            _loginRequested = false;
            Loading.IsVisible = Loading.IsRunning = false;
            UpdateButton();
            await DisplayAlertAsync("Inicio de sesión", MensajeLabel.Text, "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló una operación de inicio o autenticación.");
            MensajeLabel.Text = AuthService.UserMessage(ex);
        }
        finally
        {
            _busy = false;
            Loading.IsVisible = Loading.IsRunning = false;
            UpdateButton();
        }
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        if (_busy || _loginRequested) return;
        var mensaje = ValidacionInicioSesion.Mensaje(CorreoEntry.Text, ContrasenaEntry.Text);
        if (mensaje is not null)
        {
            MensajeLabel.Text = mensaje;
            await DisplayAlertAsync("Inicio de sesión", mensaje, "Aceptar");
            return;
        }

        _loginRequested = true;
        _restoreCancellation?.Cancel();
        UpdateButton();
        var correo = CorreoEntry.Text.Trim();
        var contrasena = ContrasenaEntry.Text;
        var recordar = RecordarmeCheck.IsChecked;
        try
        {
            await RunAsync(async () =>
            {
                while (true)
                {
                    using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var tarea = Task.Run(() => _auth.SignInAsync(correo, contrasena, recordar, limite.Token));
                    try
                    {
                        await tarea.WaitAsync(limite.Token);
                        break;
                    }
                    catch (EmailNoVerificadoException)
                    {
                        if (!await DisplayAlertAsync("Verificación de correo", "Confirma el enlace que te llegó a tu correo y presiona Reintentar.", "REINTENTAR", "CANCELAR")) return;
                    }
                    catch (OperationCanceledException) when (limite.IsCancellationRequested)
                    {
                        _ = ObservarInicioCanceladoAsync(tarea);
                        throw;
                    }
                }
                MensajeLabel.Text = "✅ Inicio de sesión exitoso, cargando la aplicación";
                ContrasenaEntry.Text = string.Empty;
                await MainThread.InvokeOnMainThreadAsync(() => ((App)Application.Current!).ShowAuthenticatedAppAsync());
            });
        }
        finally
        {
            _loginRequested = false;
            UpdateButton();
        }
    }

    private async void OnResetClicked(object? sender, EventArgs e)
    {
        _restoreCancellation?.Cancel();
        var correo = await DisplayPromptAsync("Recuperar contraseña", "Introduce tu correo electrónico:", keyboard: Keyboard.Email, initialValue: CorreoEntry.Text);
        if (correo == null) return;
        if (!MailAddress.TryCreate(correo.Trim(), out _))
        {
            MensajeLabel.Text = "Introduce un correo electrónico válido.";
            return;
        }
        await RunAsync(async () =>
        {
            await _auth.SendResetAsync(correo.Trim());
            await DisplayAlertAsync("Recuperación", "Si la cuenta existe, recibirás instrucciones por correo.", "Aceptar");
        });
    }

    private async void OnRegisterClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        _restoreCancellation?.Cancel();
        await Navigation.PushAsync(_services.GetRequiredService<RegistroPage>());
    }
}