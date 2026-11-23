using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class CotizacionesPage : ContentPage, IShellToolbarActionPage
{
    public Button ShellToolbarActionButton => NuevoContentButton;

    private readonly DatabaseService _db;
    private readonly LicenciaService _licencia;
    private readonly AuthService _auth;
    private readonly IServiceProvider _services;
    private readonly ILogger<CotizacionesPage> _logger;
    private List<Cotizacion> _cotizaciones = new();
    private string _busqueda = string.Empty;
    private bool _busquedaProfunda;
    private bool? _mobileHeader;

    public CotizacionesPage(DatabaseService db, LicenciaService licencia, AuthService auth, IServiceProvider services, ILogger<CotizacionesPage> logger)
    {
        InitializeComponent();
        _db = db; _licencia = licencia; _auth = auth; _services = services; _logger = logger;
#if ANDROID
        CotizacionesIconoLabel.IsVisible = CotizacionesTituloLabel.IsVisible = NuevoContentButton.IsVisible = false;
        AndroidMobileUi.EnableHorizontalTableScroll(TablaContainer, 720);
#endif
        ToolbarItems.Remove(ToolbarItems.First(item => item.Text == "+ Nuevo"));
        FiltroPicker.ItemsSource = new[] { "Todo", "Atrasado", "Pagado", "Sin pagar", "Enviado", "Guardado" };
        FiltroPicker.SelectedIndex = 0;
    }

    private void OnHeaderSizeChanged(object? sender, EventArgs e)
    {
        var mobile = HeaderGrid.Width < 600;
        if (_mobileHeader == mobile) return;
        _mobileHeader = mobile;
        HeaderGrid.ColumnDefinitions.Clear();
        HeaderGrid.RowDefinitions.Clear();
        if (mobile)
        {
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(HeaderActions, 0);
            Grid.SetColumn(HeaderActions, 0);
            Grid.SetColumn(SearchFilters, 0);
            Grid.SetRow(SearchFilters, 1);
            SearchFilters.HorizontalOptions = LayoutOptions.Fill;
        }
        else
        {
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(HeaderActions, 0);
            Grid.SetColumn(HeaderActions, 0);
            Grid.SetRow(SearchFilters, 0);
            Grid.SetColumn(SearchFilters, 1);
            SearchFilters.HorizontalOptions = LayoutOptions.End;
        }
    }

    protected override async void OnAppearing() { base.OnAppearing(); await CargarAsync(); }

    private async Task CargarAsync()
    {
        try
        {
            await _licencia.ReiniciarConteoSiCambioMesAsync();
            _cotizaciones = await _db.GetCotizacionesAsync(_auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para ver las cotizaciones."));
            AplicarFiltro();
            EstadoLabel.Text = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron cargar las cotizaciones.");
            EstadoLabel.Text = ex.Message;
        }
    }

    private async void OnNuevaClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!await _licencia.PuedeCrearCotizacionAsync())
            {
                var estado = await _licencia.ObtenerEstadoLicenciaAsync();
                EstadoLabel.Text = $"Has usado {estado.CotizacionesEsteMes}/5 cotizaciones este mes. Activa licencia para más.";
                return;
            }
            await Navigation.PushAsync(ActivatorUtilities.CreateInstance<CotizacionEditarPage>(_services));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo iniciar una cotización."); EstadoLabel.Text = ex.Message; }
    }

    private void OnFiltroChanged(object? sender, EventArgs e) => AplicarFiltro();
    private void OnBuscarChanged(object? sender, TextChangedEventArgs e)
    {
        _busqueda = e.NewTextValue?.Trim() ?? string.Empty;
        AplicarFiltro();
    }
    private void OnBusquedaProfundaChanged(object? sender, CheckedChangedEventArgs e)
    {
        _busquedaProfunda = e.Value;
        AplicarFiltro();
    }

    private void AplicarFiltro()
    {
        var filtro = FiltroPicker.SelectedItem?.ToString() ?? "Todo";
        var consulta = _cotizaciones.Where(c => filtro switch
        {
            "Atrasado" => c.EstadoCorto == "Atrasado",
            "Pagado" => c.Pagada,
            "Sin pagar" => !c.ConvertidaAFactura && !c.Pagada && !c.Enviada,
            "Enviado" => !c.ConvertidaAFactura && c.Enviada,
            "Guardado" => !c.Enviada && !c.ConvertidaAFactura,
            _ => true
        });
        if (_busqueda.Length > 0)
        {
            consulta = consulta.Where(c =>
                c.ClienteNombre.Contains(_busqueda, StringComparison.OrdinalIgnoreCase) ||
                c.NumeroVisual.Contains(_busqueda, StringComparison.OrdinalIgnoreCase) ||
                (_busquedaProfunda && c.Items.Any(item => item.Descripcion.Contains(_busqueda, StringComparison.OrdinalIgnoreCase))));
        }
        var cotizacionesFiltradas = consulta.OrderByDescending(c => c.Fecha).ToList();
        for (var indice = 0; indice < cotizacionesFiltradas.Count; indice++)
            cotizacionesFiltradas[indice].ColorFondoFila = indice % 2 == 0 ? Color.FromArgb("#FAFAFA") : Colors.White;
        Lista.ItemsSource = cotizacionesFiltradas;
    }

    private async void OnFilaTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not View { BindingContext: Cotizacion cotizacion }) return;
        if (cotizacion.ConvertidaAFactura)
        {
            try { await Navigation.PushAsync(ActivatorUtilities.CreateInstance<CotizacionEditarPage>(_services, cotizacion, true)); }
            catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir la cotización."); EstadoLabel.Text = "No se pudo abrir la cotización."; }
            return;
        }
        await EditarAsync(cotizacion);
    }

    private async void OnFilaAccionesClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: Cotizacion cotizacion }) return;
        var boton = (Button)sender;
        var opciones = new[] { "📧 Enviar", "📄 Vista previa", "✏️ Editar", "📋 Copiar", "💳 Convertir a factura", "🗑️ Eliminar" };
#if ANDROID
        var accion = await AndroidMobileUi.ShowRowActionsAsync(boton, $"Cotización {cotizacion.Fecha:dd/MM/yyyy}", opciones);
#else
        var accion = await DisplayActionSheetAsync($"Cotización {cotizacion.Fecha:dd/MM/yyyy}", "Cancelar", null, opciones);
#endif
        if (accion == opciones[0]) await EnviarAsync(cotizacion);
        else if (accion == opciones[1]) await MostrarPdfAsync(cotizacion);
        else if (accion == opciones[2]) await EditarAsync(cotizacion);
        else if (accion == opciones[3]) await CopiarAsync(cotizacion);
        else if (accion == opciones[4]) await ConvertirAsync(cotizacion);
        else if (accion == opciones[5]) await EliminarAsync(cotizacion);
    }

    private async Task EditarAsync(Cotizacion cotizacion)
    {
        if (cotizacion.ConvertidaAFactura) { await DisplayAlertAsync("Cotización", "Esta cotización ya fue convertida.", "Aceptar"); return; }
        try { await Navigation.PushAsync(ActivatorUtilities.CreateInstance<CotizacionEditarPage>(_services, cotizacion)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir la cotización."); EstadoLabel.Text = "No se pudo abrir la cotización."; }
    }

    private async Task MostrarPdfAsync(Cotizacion cotizacion)
    {
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.");
            var path = await new DocumentoPdfService(_db).CrearCotizacionAsync(cotizacion, uid);
            await Launcher.Default.OpenAsync(new OpenFileRequest("Vista previa de cotización", new ReadOnlyFile(path)));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo generar la vista previa de la cotización."); await DisplayAlertAsync("PDF", ex is InvalidOperationException ? ex.Message : "No se pudo generar la vista previa.", "Aceptar"); }
    }

    private async Task EnviarAsync(Cotizacion cotizacion)
    {
        if (cotizacion.ConvertidaAFactura) { await DisplayAlertAsync("Cotización", "Esta cotización ya fue convertida.", "Aceptar"); return; }
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.");
            var path = await new DocumentoPdfService(_db).CrearCotizacionAsync(cotizacion, uid);
            var canales = new[] { "Correo Electrónico", "WhatsApp / Compartir", "Guardar como PDF" };
            var canal = await DisplayActionSheetAsync("Enviar cotización", "Cancelar", null, canales);
            if (canal is null or "Cancelar") return;
            if (canal == canales[0])
            {
                var cliente = await _db.GetClienteByIdAsync(cotizacion.ClienteId);
                var destinatarios = string.IsNullOrWhiteSpace(cliente?.Correo) ? Array.Empty<string>() : new[] { cliente.Correo };
                var adjuntos = new List<EmailAttachment> { new(path) };
                var email = new EmailMessage("Cotización", "Adjuntamos la cotización solicitada.", destinatarios) { Attachments = adjuntos };
                await Email.Default.ComposeAsync(email);
            }
            else await Share.Default.RequestAsync(new ShareFileRequest("Cotización", new ShareFile(path)));
            if (canal != canales[2] && await DisplayAlertAsync("Confirmar envío", "¿Confirmas que enviaste la cotización?", "Sí", "No"))
            {
                await _db.MarcarCotizacionEnviadaAsync(cotizacion.Id, _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar."));
                await CargarAsync();
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo preparar el envío de la cotización."); await DisplayAlertAsync("Enviar", ex is InvalidOperationException ? ex.Message : "No se pudo preparar el documento.", "Aceptar"); }
    }

    private async Task CopiarAsync(Cotizacion cotizacion)
    {
        var tipos = new[] { "Para cotización", "Para factura", "Para nota de crédito" };
        var tipo = await DisplayActionSheetAsync("Copia — Seleccione tipo de documento", "Cancelar", null, tipos);
        if (tipo == tipos[0])
            await Navigation.PushAsync(ActivatorUtilities.CreateInstance<CotizacionEditarPage>(_services, cotizacion, false, true));
        else if (tipo == tipos[1])
        {
            var factura = new Factura
            {
                ClienteId = cotizacion.ClienteId, ClienteNombre = cotizacion.ClienteNombre, Fecha = DateTime.UtcNow,
                Items = cotizacion.Items, Subtotal = cotizacion.Subtotal, Itbis = cotizacion.Itbis,
                Total = cotizacion.Total, Estado = "Guardada", Nota = cotizacion.Nota,
                FirmaEmpresaRuta = cotizacion.FirmaEmpresaRuta, FirmaClienteRuta = cotizacion.FirmaClienteRuta
            };
            await Navigation.PushAsync(ActivatorUtilities.CreateInstance<FacturaEditarPage>(_services, factura, false, true));
        }
        else if (tipo == tipos[2])
            await Navigation.PushAsync(ActivatorUtilities.CreateInstance<NotaCreditoEditarPage>(_services, cotizacion.ClienteId, cotizacion.Items));
    }

    private async Task ConvertirAsync(Cotizacion c)
    {
        try
        {
            var factura = await _db.ConvertirCotizacionAFacturaAsync(c.Id, _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar."));
            await DisplayAlertAsync("Factura creada", $"La cotización se convirtió en la factura {factura.NCF}.", "Aceptar");
            await CargarAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo convertir la cotización."); await DisplayAlertAsync("No se pudo convertir", ex.Message, "Aceptar"); }
    }

    private async Task EliminarAsync(Cotizacion c)
    {
        try
        {
            if (!await DisplayAlertAsync("Eliminar cotización", "¿Deseas eliminar esta cotización?", "Eliminar", "Cancelar")) return;
            await _db.EliminarCotizacionAsync(c.Id, _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar."));
            await CargarAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo eliminar la cotización."); await DisplayAlertAsync("Error", ex.Message, "Aceptar"); }
    }

    private async void OnVolverClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("//modulo-0");
}
