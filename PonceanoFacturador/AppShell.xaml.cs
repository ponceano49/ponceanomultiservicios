using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;
using PonceanoFacturador.Views;

namespace PonceanoFacturador;

public interface IShellToolbarActionPage
{
    Button ShellToolbarActionButton { get; }
}

public partial class AppShell : Shell
{
    private readonly IServiceProvider _services;
    private readonly AuthService _auth;
    private readonly DatabaseService _db;
    private readonly ILogger<AppShell> _logger;
    private readonly List<(string Ruta, Grid Vista)> _vistasMenu = new();
    private readonly HashSet<string> _rutasRestringidas = new(StringComparer.Ordinal);
    private readonly Label _estadoActualizacion = new() { TextColor = Color.FromArgb("#263746"), Padding = new Thickness(8), IsVisible = false };
    private readonly Label _avisoAdministrador = new() { TextColor = Color.FromArgb("#713E00"), Padding = new Thickness(8), IsVisible = false };
    private bool _alertasInicialesMostradas;

    public AppShell(IServiceProvider services, AuthService auth, DatabaseService db, ILogger<AppShell> logger)
    {
        InitializeComponent();
        _services = services;
        _auth = auth;
        _db = db;
        _logger = logger;
#if ANDROID
        FlyoutBehavior = FlyoutBehavior.Flyout;
        FlyoutWidth = 300;
#endif
        AgregarElemento<FacturasPage>("▤  Facturas");
        AgregarElemento<CotizacionesPage>("▦  Cotizaciones");
        AgregarElemento<NotasCreditoPage>("▧  Notas de crédito");
        AgregarElemento<NotasDebitoPage>("▧  Notas de débito");
        AgregarElemento<ClientesPage>("♟  Clientes");
        AgregarElemento<ArticulosPage>("▣  Artículos");
        AgregarElemento<TurnosPage>("◷  Turnos y caja");
        AgregarElemento<ComprasPage>("▣  Compras e inventario");
        AgregarElemento<RobustezPage>("⚙  Robustez y mantenimiento", requiereAdministrador: true);
        AgregarPendiente("✍  Documentos firmados", "Los documentos firmados se podrán consultar aquí cuando se integre el servicio de firma electrónica.");
        AgregarElemento<ResumenPage>("▥  Resumen");
        AgregarElemento<AjustesPage>("☷  Ajustes");
        AgregarElemento<SoportePage>("◎  Soporte");
        AgregarCerrarSesion();
        var dgii = new FlyoutItem { Title = "⚡  DGII Automática", Route = "modulo-dgii" };
        const string rutaContenidoDgii = "contenido-dgii";
        dgii.Items.Add(new ShellContent
        {
            Title = dgii.Title,
            Route = rutaContenidoDgii,
            ContentTemplate = new DataTemplate(() => _services.GetRequiredService<CapturaFacturasDGII>())
        });
        _rutasRestringidas.Add(dgii.Route);
        _rutasRestringidas.Add(rutaContenidoDgii);
        Items.Insert(8, dgii);
        FlyoutContent = CrearMenuLateral();
        CurrentItem = Items.First(item => item.Title == "▤  Facturas");
        ActualizarSeleccionMenu();
        Navigating += OnShellNavigating;
        Navigated += OnShellNavigated;
    }

    public void MostrarAvisoAdministradorSinNtp(string mensaje)
    {
        _avisoAdministrador.Text = mensaje;
        _avisoAdministrador.IsVisible = true;
    }

    private View CrearMenuLateral()
    {
        var contenido = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(0, 8), BackgroundColor = Color.FromArgb("#F4F4F4") };
        contenido.Children.Add(new Label
        {
            Text = "Ponceano Facturador",
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#425A70"),
            Padding = new Thickness(18, 12, 12, 16)
        });
        contenido.Children.Add(_avisoAdministrador);
        contenido.Children.Add(_estadoActualizacion);
        foreach (var item in Items)
        {
            var ruta = item.Route;
            if (item.Title is "▥  Resumen" or "☷  Ajustes")
                contenido.Children.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#DDDDDD"), Margin = new Thickness(0, 5) });

            var partes = item.Title.Split(new[] { "  " }, 2, StringSplitOptions.None);
            var icono = partes.Length == 2 ? partes[0] : string.Empty;
            var titulo = partes.Length == 2 ? partes[1] : item.Title;
            var fila = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection { new(32), new(GridLength.Star) },
                Padding = new Thickness(16, 4),
                MinimumHeightRequest = 56,
                BackgroundColor = Colors.Transparent
            };
            fila.Add(new Label { Text = icono, FontSize = 17, TextColor = Color.FromArgb("#687D90"), VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.Start }, 0);
            fila.Add(new Label { Text = titulo, FontSize = 16, TextColor = Color.FromArgb("#60788F"), VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.WordWrap }, 1);
            fila.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await AbrirModuloAsync(ruta)) });
            var puntero = new PointerGestureRecognizer();
            puntero.PointerEntered += (_, _) => { if (!EsModuloActual(ruta)) fila.BackgroundColor = Color.FromArgb("#ECECEC"); };
            puntero.PointerExited += (_, _) => fila.BackgroundColor = EsModuloActual(ruta) ? Color.FromArgb("#E8E8E8") : Colors.Transparent;
            fila.GestureRecognizers.Add(puntero);
            _vistasMenu.Add((ruta, fila));
            contenido.Children.Add(fila);
        }
        return new MenuLateralScrollView { BackgroundColor = Color.FromArgb("#F4F4F4"), Content = contenido };
    }

    private bool EsModuloActual(string? ruta) => CurrentItem?.Route == ruta;

    private void ActualizarSeleccionMenu()
    {
        foreach (var (ruta, vista) in _vistasMenu)
            vista.BackgroundColor = EsModuloActual(ruta) ? Color.FromArgb("#E8E8E8") : Colors.Transparent;
    }

    private bool _cambiandoModulo;

    private async Task AbrirModuloAsync(string? ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta) || _cambiandoModulo) return;
        _cambiandoModulo = true;
        try
        {
            if (RequiereAdministrador(ruta) && !await ConfirmarAccesoAdministradorAsync()) return;
            var paginas = Navigation.NavigationStack.ToList();
            foreach (var documento in paginas.OfType<IDocumentoEditable>().Reverse())
                if (!await DocumentoNavegacion.ConfirmarSalidaAsync(CurrentPage, documento)) return;
            if (paginas.OfType<IDocumentoEditable>().Any())
                await Navigation.PopToRootAsync();
#if ANDROID
            FlyoutIsPresented = false;
#endif
            await GoToAsync(ruta.StartsWith("//", StringComparison.Ordinal) ? ruta : $"//{ruta}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo navegar al módulo {Ruta}.", ruta);
            await DisplayAlertAsync("Navegación", "No se pudo abrir el módulo seleccionado.", "Aceptar");
        }
        finally { _cambiandoModulo = false; }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        AplicarBarraNavegacionAndroid();
#endif
        if (_alertasInicialesMostradas) return;
        _alertasInicialesMostradas = true;
        var usuarioId = _auth.CurrentUser?.IdFirebase;
        if (string.IsNullOrWhiteSpace(usuarioId)) return;
        try
        {
            var alertas = new List<string>();
            var hoy = DateTime.Today;
            var claveRespaldo = "respaldo_diario_" + usuarioId;
            if (!string.Equals(Preferences.Default.Get(claveRespaldo, string.Empty), hoy.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                try
                {
#if WINDOWS
                    var documentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    if (string.IsNullOrWhiteSpace(documentos)) throw new InvalidOperationException("No se encontró la carpeta Documentos.");
                    var carpetaRespaldo = Path.Combine(documentos, "PonceanoFacturador", "Respaldos");
#else
                    var carpetaRespaldo = Path.Combine(FileSystem.AppDataDirectory, "Respaldos");
#endif
                    var rutaRespaldo = await _db.CrearRespaldoBaseDatosAsync(usuarioId, carpetaRespaldo);
                    Preferences.Default.Set(claveRespaldo, hoy.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                    alertas.Add($"✅ Respaldo diario creado: {rutaRespaldo}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "No se pudo crear el respaldo diario de la base local.");
                    alertas.Add("⚠️ No se pudo crear el respaldo diario; realiza un respaldo manual desde Robustez y mantenimiento.");
                }
            }

            var configuracionesAlerta = await _db.ObtenerConfiguracionesAlertasAsync(usuarioId);
            bool AlertaActivaYVencida(string tipo)
            {
                var configuracion = configuracionesAlerta.FirstOrDefault(item => item.TipoAlerta == tipo);
                if (configuracion is null) return true;
                if (!configuracion.Activa) return false;
                if (DateOnly.TryParseExact(configuracion.FechaAviso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var fechaAviso) && hoy < fechaAviso.ToDateTime(TimeOnly.MinValue)) return false;
                if (TimeOnly.TryParse(configuracion.HoraAviso, System.Globalization.CultureInfo.InvariantCulture, out var horaAviso) && TimeOnly.FromDateTime(DateTime.Now) < horaAviso) return false;
                return true;
            }

            if (AlertaActivaYVencida("Vencimiento DGII") && configuracionesAlerta.Any(item => item.TipoAlerta == "Vencimiento DGII" && item.Activa && !string.IsNullOrWhiteSpace(item.FechaAviso)))
                alertas.Add("Recordatorio configurado: revisar el envío de los formatos DGII 606/607.");
            if (AlertaActivaYVencida("Stock bajo"))
            {
                var bajoMinimo = await _db.GetArticulosBajoMinimoAsync();
                if (bajoMinimo.Count > 0) alertas.Add($"Productos bajo stock mínimo ({bajoMinimo.Count}): {string.Join(", ", bajoMinimo.Take(6).Select(articulo => articulo.Descripcion))}.");
            }
            if (AlertaActivaYVencida("Serie NCF por agotarse"))
            {
                var ncf = await _db.ObtenerConfiguracionNcfAsync(usuarioId);
                var agotandose = ncf.Where(config => config.Activo && config.NumeroFinal >= config.SiguienteNumero && config.NumeroFinal - config.SiguienteNumero + 1 < 50).ToList();
                if (agotandose.Count > 0) alertas.Add("NCF con menos de 50 disponibles: " + string.Join(", ", agotandose.Select(config => $"{config.Tipo} ({config.NumeroFinal - config.SiguienteNumero + 1})")) + ".");
            }
            if (AlertaActivaYVencida("Facturas pendientes"))
            {
                var sinNcf = (await _db.GetFacturasAsync(usuarioId)).Count(factura => factura.Estado == "Guardada" && string.IsNullOrWhiteSpace(factura.NCF));
                if (sinNcf > 0) alertas.Add($"Facturas guardadas sin NCF asignado: {sinNcf}.");
            }
            if (alertas.Count > 0) await DisplayAlertAsync("Alertas y respaldos", string.Join("\n\n", alertas), "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron cargar los respaldos diarios y las alertas configuradas.");
        }
        await ConsultarActualizacionAsync();
    }

    private async Task ConsultarActualizacionAsync()
    {
#if WINDOWS || ANDROID
        var servicio = _services.GetRequiredService<ActualizacionService>();
        if (!servicio.IniciarUnaVez()) return;
        try
        {
            var actualizacion = await servicio.ConsultarAsync(AppInfo.Current.VersionString, DeviceInfo.Current.Platform == DevicePlatform.WinUI);
            if (actualizacion is null || !ReferenceEquals(Application.Current?.Windows.FirstOrDefault()?.Page, this)) return;
            if (!await DisplayAlertAsync("Actualización disponible", $"✅ HAY UNA NUEVA ACTUALIZACIÓN DISPONIBLE (v{actualizacion.Version}). ¿DESEA DESCARGAR LA ACTUALIZACIÓN AHORA?", "DESCARGAR ACTUALIZACIÓN", "POSPONER")) return;
            _ = DescargarActualizacionAsync(actualizacion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            _logger.LogWarning(ex, "No se pudo consultar la actualización; la aplicación continúa normalmente.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la actualización.");
            if (ReferenceEquals(Application.Current?.Windows.FirstOrDefault()?.Page, this))
                await DisplayAlertAsync("Actualización", "No se pudo abrir la descarga. Puedes seguir trabajando y volver a intentarlo al iniciar la aplicación.", "Aceptar");
        }
#endif
    }

    private void MostrarEstadoActualizacion(string mensaje)
    {
        _estadoActualizacion.Text = mensaje;
        _estadoActualizacion.IsVisible = true;
#if ANDROID
        Android.Widget.Toast.MakeText(Android.App.Application.Context, mensaje, Android.Widget.ToastLength.Long)?.Show();
#endif
    }

    private async Task DescargarActualizacionAsync(ActualizacionDisponible actualizacion)
    {
        MostrarEstadoActualizacion("Iniciando descarga de la actualización…");
        try
        {
            var servicio = _services.GetRequiredService<DescargaActualizacionService>();
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var esWindows = DeviceInfo.Current.Platform == DevicePlatform.WinUI;
            var directorio = FileSystem.CacheDirectory;
            var ruta = await Task.Run(() => servicio.DescargarAsync(actualizacion.Enlace, directorio, esWindows, limite.Token));
            MostrarEstadoActualizacion("Actualización descargada. Selecciona dónde guardarla o abrirla.");
            if (ReferenceEquals(Application.Current?.Windows.FirstOrDefault()?.Page, this))
                await Share.Default.RequestAsync(new ShareFileRequest("Guardar actualización descargada", new ShareFile(ruta)));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo descargar la actualización desde {Servidor}.", actualizacion.Enlace.Host);
            var mensaje = "⚠️ Error al descargar la actualización. Puede descargar manualmente desde la página web.";
            MostrarEstadoActualizacion(mensaje);
            if (ReferenceEquals(Application.Current?.Windows.FirstOrDefault()?.Page, this))
                await DisplayAlertAsync("Actualización", mensaje, "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo compartir la actualización descargada.");
            MostrarEstadoActualizacion("⚠️ No se pudo compartir la actualización descargada. Puede descargarla manualmente desde la página web.");
        }
    }

    private async Task<bool> ConfirmarAccesoAdministradorAsync()
    {
        if (_auth.EsAdministrador) return true;

        var dialogo = new DialogoPermisoAdministrador();
        await Navigation.PushModalAsync(dialogo);
        var password = await dialogo.Resultado;
        if (Navigation.ModalStack.Contains(dialogo))
            await Navigation.PopModalAsync();
        if (string.IsNullOrEmpty(password)) return false;

        try
        {
            var autorizado = await _auth.ValidarContraseñaMaestraAsync(password);
            password = string.Empty;
            if (autorizado) return true;
            await DisplayAlertAsync("Acceso denegado", "La contraseña de administrador es incorrecta.", "Aceptar");
            return false;
        }
        catch (Exception ex) when (ex is AuthException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "No se pudo validar la contraseña maestra.");
            await DisplayAlertAsync("Validación de administrador", AuthService.UserMessage(ex), "Aceptar");
            return false;
        }
        finally
        {
            password = string.Empty;
        }
    }

    private bool RequiereAdministrador(string? ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return false;
        var rutaSinConsulta = ruta.Split('?', 2)[0];
        return rutaSinConsulta.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(_rutasRestringidas.Contains);
    }

    private void AgregarElemento<TPage>(string titulo, bool requiereAdministrador = false) where TPage : Page
    {
        var rutaModulo = $"modulo-{Items.Count}";
        var rutaContenido = $"contenido-{Items.Count}";
        var item = new FlyoutItem { Title = titulo, Route = rutaModulo };
        item.Items.Add(new ShellContent
        {
            Title = titulo,
            Route = rutaContenido,
            ContentTemplate = new DataTemplate(() => _services.GetRequiredService<TPage>())
        });
        Items.Add(item);
        if (requiereAdministrador)
        {
            _rutasRestringidas.Add(rutaModulo);
            _rutasRestringidas.Add(rutaContenido);
        }
    }

    private void AgregarPendiente(string titulo, string descripcion)
    {
        var item = new FlyoutItem { Title = titulo, Route = $"modulo-{Items.Count}" };
        item.Items.Add(new ShellContent
        {
            Title = titulo,
            Route = $"contenido-{Items.Count}",
            ContentTemplate = new DataTemplate(() => new ModuloPendientePage(titulo, descripcion))
        });
        Items.Add(item);
    }

    private void AgregarCerrarSesion()
    {
        var item = new FlyoutItem { Title = "⇥  Cerrar sesión", Route = $"modulo-{Items.Count}" };
        item.Items.Add(new ShellContent
        {
            Title = item.Title,
            Route = $"contenido-{Items.Count}",
            ContentTemplate = new DataTemplate(() => new ContentPage { Title = item.Title, Content = new Grid() })
        });
        Items.Add(item);
    }

    private async void OnShellNavigating(object? sender, ShellNavigatingEventArgs e)
    {
        if (_cambiandoModulo) return;
        var destino = e.Target.Location.OriginalString;
        if (RequiereAdministrador(destino))
        {
            e.Cancel();
            await AbrirModuloAsync(destino);
            return;
        }

        var paginas = Navigation.NavigationStack;
        if (e.Source != ShellNavigationSource.ShellItemChanged || paginas is null ||
            !paginas.OfType<IDocumentoEditable>().Any()) return;
        e.Cancel();
        await AbrirModuloAsync(destino);
    }

    #if ANDROID
    private void AplicarBarraNavegacionAndroid()
    {
        if (CurrentPage is not ContentPage page) return;

        Shell.SetNavBarIsVisible(page, true);
        var esSubpantalla = page.Navigation.NavigationStack.Count > 1;
        var accion = (page as IShellToolbarActionPage)?.ShellToolbarActionButton;
        var anchoIzquierdo = esSubpantalla ? 48 : accion is not null ? 30 : 0;
        var anchoDerecho = accion is not null ? 100 : esSubpantalla ? 116 : 0;
        var barra = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(anchoIzquierdo),
                new(GridLength.Star),
                new(anchoDerecho)
            },
            Padding = new Thickness(4, 0),
            HeightRequest = 48
        };

        if (esSubpantalla)
        {
            var menuButton = new Button
            {
                Text = "☰",
                FontSize = 22,
                TextColor = Color.FromArgb("#536B80"),
                BackgroundColor = Colors.Transparent,
                Padding = 0,
                MinimumWidthRequest = 48,
                MinimumHeightRequest = 48,
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Center
            };
            SemanticProperties.SetDescription(menuButton, "Abrir menú de navegación");
            menuButton.Clicked += (_, _) => FlyoutIsPresented = true;
            barra.Add(menuButton, 0);
        }

        var titulo = new Label
        {
            Text = page.Title,
            FontFamily = "OpenSansSemibold",
            FontSize = 18,
            TextColor = Color.FromArgb("#425A70"),
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            TranslationX = !esSubpantalla && accion is null ? -34 : 0
        };
        barra.Add(titulo, 1);

        if (accion is not null)
        {
            if (accion.Parent is Layout contenedor)
                contenedor.Remove(accion);
            accion.IsVisible = true;
            accion.WidthRequest = 96;
            accion.MinimumWidthRequest = 96;
            accion.HeightRequest = 40;
            accion.MinimumHeightRequest = 40;
            accion.HorizontalOptions = LayoutOptions.Center;
            accion.VerticalOptions = LayoutOptions.Center;
            barra.Add(accion, 2);
        }

        Shell.SetTitleView(page, barra);
    }
#endif

    private async void OnShellNavigated(object? sender, ShellNavigatedEventArgs e)
    {
        ActualizarSeleccionMenu();
#if ANDROID
        AplicarBarraNavegacionAndroid();
#endif
        if (CurrentItem?.Title != "⇥  Cerrar sesión") return;
        try
        {
            _auth.SignOut();
            ((App)Application.Current!).ShowLogin();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cerrar sesión", $"No se pudo cerrar la sesión: {ex.Message}", "Aceptar");
            CurrentItem = Items.First(item => item.Title == "▤  Facturas");
        }
    }
}
