using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;
using PonceanoFacturador.Views;

namespace PonceanoFacturador
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });
#if ANDROID
            builder.ConfigureMauiHandlers(handlers =>
                handlers.AddHandler<MenuLateralScrollView, Platforms.Android.MenuLateralScrollViewHandler>());
#endif

            builder.Services.AddSingleton<DatabaseService>();
            builder.Services.AddSingleton<IPreprocesadorFacturaOcr, PreprocesadorFacturaOcr>();
            builder.Services.AddSingleton<TesseractOcrService>();
            builder.Services.AddSingleton<ImpresionWindowsService>();
            builder.Services.AddSingleton<FacturaOcrService>(_ => new FacturaOcrService(new HttpClient { Timeout = TimeSpan.FromMinutes(2) }));
            builder.Services.AddSingleton<LecturaDocumentoFiscalService>(_ => new LecturaDocumentoFiscalService(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromMinutes(10) }));
#if WINDOWS
            builder.Services.AddSingleton<RegistroLecturaOcrService>();
#else
            builder.Services.AddSingleton(_ => new RegistroLecturaOcrService(Path.Combine(FileSystem.AppDataDirectory, "Reportes", "OCR")));
#endif
            builder.Services.AddSingleton<ValidacionFiscalOllamaService>(_ => new ValidacionFiscalOllamaService(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromMinutes(5) }));
            builder.Services.AddSingleton<BorradorFiscal606607OllamaService>(_ => new BorradorFiscal606607OllamaService(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromMinutes(5) }));
            builder.Services.AddSingleton<DirectorioProveedoresService>();
            builder.Services.AddSingleton<ExportacionFiscalPreliminarService>();
            builder.Services.AddSingleton<FormatoEnvioDgiiService>();
            builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
            builder.Services.AddSingleton<AuthService>();
            builder.Services.AddSingleton<LicenciaService>();
            builder.Services.AddSingleton(_ => new LicenciaRemotaService(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) }));
            builder.Services.AddSingleton<PayPalCheckoutService>(services => new PayPalCheckoutService(
                new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) },
                services.GetRequiredService<AuthService>(), services.GetRequiredService<LicenciaRemotaService>(),
                services.GetRequiredService<DatabaseService>()));
            builder.Services.AddSingleton<RelojNtpService>();
            builder.Services.AddSingleton<IRelojNtpService>(services => services.GetRequiredService<RelojNtpService>());
            builder.Services.AddSingleton<IdentificadorDispositivoService>();
            builder.Services.AddSingleton<LicenciaPruebaService>();
            builder.Services.AddSingleton<ValidacionLicenciaNtpService>();
            builder.Services.AddSingleton(_ => new ActualizacionService(
                new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) },
                new Uri(ActualizacionService.DireccionManifiesto)));
            builder.Services.AddSingleton(_ => new DescargaActualizacionService(
                new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }) { Timeout = TimeSpan.FromSeconds(20) }));
            builder.Services.AddSingleton<CapturaFacturasDgiiService>();
            builder.Services.AddSingleton(_ => new ListaLocalRncService(
                Path.Combine(FileSystem.AppDataDirectory, "lista-rnc-local.json"),
                Path.Combine(FileSystem.AppDataDirectory, "Lista_RNC_Proveedores.csv"),
                () => FileSystem.OpenAppPackageFileAsync("Lista_RNC_Proveedores.csv")));
            builder.Services.AddSingleton(_ => new RncContribuyentesService(
                Path.Combine(FileSystem.AppDataDirectory, "rnc-contribuyentes.db3"),
                Path.Combine(FileSystem.AppDataDirectory, "RNC_CONTRIBUYENTES"),
                () => FileSystem.OpenAppPackageFileAsync("RNC_CONTRIBUYENTES.zip")));
            builder.Services.AddTransient<CapturaFacturasDGII>();
            builder.Services.AddTransient<Envio606Page>();
            builder.Services.AddTransient<LoginPage>();
            builder.Services.AddTransient<RegistroPage>();
            builder.Services.AddTransient<ClientesPage>();
            builder.Services.AddTransient<CotizacionesPage>();
            builder.Services.AddTransient<CotizacionEditarPage>();
            builder.Services.AddTransient<FacturasPage>();
            builder.Services.AddTransient<FacturaEditarPage>();
            builder.Services.AddTransient<LicenciaPage>();
            builder.Services.AddTransient<ActivacionLicenciaPage>();
            builder.Services.AddTransient<ArticulosPage>();
            builder.Services.AddTransient<TurnosPage>();
            builder.Services.AddTransient<ComprasPage>();
            builder.Services.AddTransient<RobustezPage>();
            builder.Services.AddTransient<AjustesPage>();
            builder.Services.AddTransient<CompaniaPage>();
            builder.Services.AddTransient<NotasCreditoPage>();
            builder.Services.AddTransient<NotaCreditoEditarPage>();
            builder.Services.AddTransient<NotasDebitoPage>();
            builder.Services.AddTransient<ResumenPage>();
            builder.Services.AddTransient<PaginaDGII>();
            builder.Services.AddTransient<ExportarDatosPage>();
            builder.Services.AddTransient<SoportePage>();
            builder.Services.AddTransient<MainPage>();
            builder.Services.AddTransient<AppShell>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
