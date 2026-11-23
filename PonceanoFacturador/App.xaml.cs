using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;
using PonceanoFacturador.Views;

namespace PonceanoFacturador
{
    public partial class App : Application
    {
        private readonly IServiceProvider _services;
        private readonly ValidacionLicenciaNtpService _validacionLicencia;
        private readonly ILogger<App> _logger;

        public App(IServiceProvider services, ValidacionLicenciaNtpService validacionLicencia, ILogger<App> logger)
        {
            InitializeComponent();
            _services = services;
            _validacionLicencia = validacionLicencia;
            _logger = logger;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new NavigationPage(_services.GetRequiredService<LoginPage>()));
#if WINDOWS
            window.Created += (_, _) =>
            {
                if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow &&
                    nativeWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                    presenter.Maximize();
            };
#endif
            return window;
        }

        public async Task<bool> ShowAuthenticatedAppAsync()
        {
            if (Current?.Windows.FirstOrDefault() is not { Page: { } paginaActual } window)
                throw new InvalidOperationException("No se encontró la ventana de la aplicación.");

            ResultadoValidacionLicenciaInicial resultado;
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                resultado = await _validacionLicencia.ValidarAntesDeEntrarAsync(limite.Token).WaitAsync(limite.Token);
            }
            catch (OperationCanceledException) when (limite.IsCancellationRequested)
            {
                _logger.LogWarning("La validación de licencia excedió el tiempo límite.");
                await paginaActual.DisplayAlertAsync("Validación de licencia", "⚠️ No se pudo validar la licencia a tiempo. Comprueba la conexión e inténtalo de nuevo.", "Aceptar");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo validar la licencia antes de abrir la aplicación.");
                await paginaActual.DisplayAlertAsync("Validación de licencia", "⚠️ NO SE PUDO VALIDAR LA LICENCIA. Comprueba la conexión e inténtalo de nuevo.", "Aceptar");
                return false;
            }

            var etapa = "preparar la pantalla de licencia";
            try
            {
                if (resultado.RequiereActivacionPrueba || !resultado.Permitida)
                {
                    var paginaLicencia = _services.GetRequiredService<ActivacionLicenciaPage>();
                    paginaLicencia.Configurar(resultado);
                    etapa = "abrir la pantalla de licencia";
                    window.Page = new NavigationPage(paginaLicencia);
                    return false;
                }

                etapa = "preparar el menú principal";
                var shell = _services.GetRequiredService<AppShell>();
                if (resultado.AdministradorSinNtp)
                {
                    shell.MostrarAvisoAdministradorSinNtp(resultado.Mensaje);
                    etapa = "mostrar el aviso de administrador";
                    await paginaActual.DisplayAlertAsync("Modo Administrador Offline", resultado.Mensaje, "Continuar");
                }
                else if (resultado.AccesoPruebaDebug)
                {
                    shell.MostrarAvisoAdministradorSinNtp(resultado.Mensaje);
                }
                else if (!string.IsNullOrWhiteSpace(resultado.Mensaje))
                {
                    await paginaActual.DisplayAlertAsync("Licencia remota", resultado.Mensaje, "Continuar");
                }
                etapa = "abrir el menú principal";
                window.Page = shell;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo completar la etapa de inicio: {Etapa}.", etapa);
#if ANDROID && DEBUG
                Android.Util.Log.Error("PonceanoInicio", ex.ToString());
#endif
                var verDetalles = await paginaActual.DisplayAlertAsync("Inicio de la aplicación", $"No se pudo {etapa}. Puedes reintentar o consultar los detalles técnicos para soporte. Los detalles pueden contener información del dispositivo.", "Ver detalles", "Cerrar");
                if (verDetalles)
                    await paginaActual.DisplayAlertAsync("Detalles técnicos", $"Etapa: {etapa}\n\n{ex}", "Cerrar");
                return false;
            }
        }

        public void ShowLogin()
        {
            if (Current?.Windows.FirstOrDefault() is not { } window)
                throw new InvalidOperationException("No se encontró la ventana de la aplicación.");
            window.Page = new NavigationPage(_services.GetRequiredService<LoginPage>());
        }
    }
}