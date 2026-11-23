using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class FacturasPage : ContentPage, IShellToolbarActionPage
{
    public Button ShellToolbarActionButton => NuevoContentButton;

    private readonly DatabaseService _db;
    private readonly LicenciaService _licencia;
    private readonly RelojNtpService _relojNtp;
    private readonly AuthService _auth;
    private readonly IServiceProvider _services;
    private readonly ImpresionWindowsService _impresion;
    private readonly ILogger<FacturasPage> _logger;
    private List<Factura> _facturas = new();
    private string _busqueda = string.Empty;
    private bool _busquedaProfunda;
    private bool? _mobileHeader;

    public FacturasPage(DatabaseService db, LicenciaService licencia, RelojNtpService relojNtp, AuthService auth, IServiceProvider services, ImpresionWindowsService impresion, ILogger<FacturasPage> logger)
    {
        InitializeComponent(); _db = db; _licencia = licencia; _relojNtp = relojNtp; _auth = auth; _services = services; _impresion = impresion; _logger = logger;
#if ANDROID
        FacturaIconoLabel.IsVisible = FacturaTituloLabel.IsVisible = NuevoContentButton.IsVisible = false;
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
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(HeaderActions, 0);
            Grid.SetColumn(HeaderActions, 0);
            Grid.SetColumn(SearchFilters, 0);
            Grid.SetRow(SearchFilters, 1);
            Grid.SetColumnSpan(SearchFilters, 1);
            Grid.SetRow(DiasPruebaLabel, 2);
            Grid.SetColumnSpan(DiasPruebaLabel, 1);
            SearchFilters.HorizontalOptions = LayoutOptions.Start;
        }
        else
        {
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(HeaderActions, 0);
            Grid.SetColumn(HeaderActions, 0);
            Grid.SetRow(SearchFilters, 0);
            Grid.SetColumn(SearchFilters, 1);
            Grid.SetColumnSpan(DiasPruebaLabel, 2);
            Grid.SetRow(DiasPruebaLabel, 1);
            SearchFilters.HorizontalOptions = LayoutOptions.End;
        }
    }

    protected override async void OnAppearing() { base.OnAppearing(); await CargarAsync(); }

    private async Task CargarAsync()
    {
        try
        {
            await _licencia.ReiniciarConteoSiCambioMesAsync();
            var estadoLicencia = await _licencia.ObtenerEstadoLicenciaAsync();
            DiasPruebaLabel.IsVisible = estadoLicencia.FechaInicioPruebaUtc.HasValue && estadoLicencia.FechaVencimientoPruebaUtc.HasValue;
            if (DiasPruebaLabel.IsVisible)
            {
                try
                {
                    var horaNtp = await _relojNtp.ObtenerHoraUtcAsync();
                    var diasRestantes = Math.Max(0, (int)Math.Ceiling((LicenciaPruebaService.AsegurarUtc(estadoLicencia.FechaVencimientoPruebaUtc!.Value) - horaNtp.UtcDateTime).TotalDays));
                    DiasPruebaLabel.Text = $"DÍAS RESTANTES DE PRUEBA: {diasRestantes} DÍAS";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo actualizar el contador de la prueba con hora NTP.");
                    DiasPruebaLabel.Text = "DÍAS RESTANTES DE PRUEBA: requiere conexión a internet para validar la hora";
                }
            }
            _facturas = await _db.GetFacturasAsync(_auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para ver las facturas."));
            AplicarFiltro();
            EstadoLabel.Text = string.Empty;
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudieron cargar las facturas."); EstadoLabel.Text = ex.Message; }
    }

    private async void OnImpresorasClicked(object? sender, EventArgs e) => await AbrirImpresorasAsync();

    private async void OnNuevaClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!await _licencia.PuedeCrearFacturaAsync())
            {
                var consumo = await _licencia.ObtenerConsumoLiteFacturasAsync();
                EstadoLabel.Text = $"Límite LITE: {consumo.Ultimos30Dias}/5 facturas en 30 días y {consumo.EsteAnio}/40 este año. Consulta Mi Suscripción para actualizar.";
                return;
            }
            await Navigation.PushAsync(ActivatorUtilities.CreateInstance<FacturaEditarPage>(_services));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo iniciar una factura."); EstadoLabel.Text = ex.Message; }
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
        var consulta = _facturas.Where(f => filtro switch
        {
            "Atrasado" => f.Estado == "Emitida" && f.EstadoPago != "Pagada" && f.VenceFecha is DateTime vence && vence.ToLocalTime().Date < DateTime.Today,
            "Pagado" => f.EstadoPago == "Pagada",
            "Sin pagar" => f.Estado == "Emitida" && f.EstadoPago != "Pagada" && !f.Enviada,
            "Enviado" => f.Enviada,
            "Guardado" => f.Estado == "Guardada",
            _ => true
        });
        if (_busqueda.Length > 0)
        {
            consulta = consulta.Where(f =>
                f.ClienteNombre.Contains(_busqueda, StringComparison.OrdinalIgnoreCase) ||
                f.NCF.Contains(_busqueda, StringComparison.OrdinalIgnoreCase) ||
                (_busquedaProfunda && (f.ClienteRnc.Contains(_busqueda, StringComparison.OrdinalIgnoreCase) ||
                    f.Items.Any(item => item.Descripcion.Contains(_busqueda, StringComparison.OrdinalIgnoreCase)))));
        }
        var facturasFiltradas = consulta.OrderByDescending(f => f.Fecha).ThenByDescending(f => f.NCF).ToList();
        for (var indice = 0; indice < facturasFiltradas.Count; indice++)
            facturasFiltradas[indice].ColorFondoFila = indice % 2 == 0 ? Color.FromArgb("#FAFAFA") : Colors.White;
        Lista.ItemsSource = facturasFiltradas;
    }

    private async void OnFilaTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not View { BindingContext: Factura factura }) return;
        await ModificarAsync(factura);
    }

    private async void OnFilaAccionesClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: Factura factura }) return;
        var boton = (Button)sender;
        var opciones = new[] { "📧 Enviar", "📄 Vista previa factura", "🖨️ Vista previa / imprimir factura", "🧾 Vista previa / imprimir recibo térmico", "🖨️ Configurar impresoras de Windows", "✏️ Editar", "📋 Copiar", "💳 Pago", "🗑️ Anular" };
#if ANDROID
        var accion = await AndroidMobileUi.ShowRowActionsAsync(boton, $"Factura {factura.NCF}", opciones);
#else
        var accion = await DisplayActionSheetAsync($"Factura {factura.NCF}", "Cancelar", null, opciones);
#endif
        if (accion == opciones[0]) await EnviarAsync(factura);
        else if (accion == opciones[1]) await CompartirPdfAsync(factura);
        else if (accion == opciones[2]) await AbrirPdfImprimibleAsync(factura, false);
        else if (accion == opciones[3]) await AbrirPdfImprimibleAsync(factura, true);
        else if (accion == opciones[4]) await AbrirImpresorasAsync();
        else if (accion == opciones[5]) await ModificarAsync(factura);
        else if (accion == opciones[6]) await CopiarAsync(factura);
        else if (accion == opciones[7]) await MostrarPagoAsync(factura);
        else if (accion == opciones[8]) await AnularAsync(factura);
    }

    private async Task ModificarAsync(Factura factura)
    {
        var soloLectura = factura.Anulada || factura.Estado != "Guardada";
        try { await Navigation.PushAsync(ActivatorUtilities.CreateInstance<FacturaEditarPage>(_services, factura, soloLectura)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir la factura."); EstadoLabel.Text = "No se pudo abrir la factura."; }
    }

    private async Task CompartirPdfAsync(Factura factura)
    {
        if (factura.NCF.Length == 0) { await DisplayAlertAsync("PDF", "El borrador aún no tiene un NCF emitido.", "Aceptar"); return; }
        try
        {
            var path = await GenerarPdfAsync(factura);
            await Launcher.Default.OpenAsync(new OpenFileRequest($"Factura {factura.NCF}", new ReadOnlyFile(path)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo generar o abrir el PDF de la factura.");
            await DisplayAlertAsync("PDF", ex is InvalidOperationException ? ex.Message : "No se pudo generar o abrir el PDF de la factura.", "Aceptar");
        }
    }

    private async Task<string> GenerarPdfAsync(Factura factura)
    {
        var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para exportar la factura.");
        return await new DocumentoPdfService(_db).CrearFacturaAsync(factura, uid);
    }

    private async Task AbrirPdfImprimibleAsync(Factura factura, bool recibo)
    {
        if (factura.Estado != "Emitida" || factura.Anulada || string.IsNullOrWhiteSpace(factura.NCF))
        {
            await DisplayAlertAsync("Impresión", "Solo se pueden imprimir facturas emitidas y vigentes.", "Aceptar");
            return;
        }
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para imprimir.");
            var ruta = recibo
                ? await new ReciboTermicoPdfService(_db).CrearAsync(factura, uid)
                : await GenerarPdfAsync(factura);
            await _impresion.AbrirVistaPreviaAsync(ruta, recibo ? $"Recibo {factura.NCF}" : $"Factura {factura.NCF}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo preparar la impresión de la factura {Ncf}.", factura.NCF);
            await DisplayAlertAsync("Impresión", ex is InvalidOperationException ? ex.Message : "No se pudo abrir la vista previa/impresión.", "Aceptar");
        }
    }

    private async Task AbrirImpresorasAsync()
    {
        try
        {
            var acciones = new[] { "Elegir y recordar impresora", "Abrir configuración de Windows" };
            var accion = await DisplayActionSheetAsync("Impresoras", "Cancelar", null, acciones);
            if (accion == acciones[0]) await _impresion.SeleccionarImpresoraAsync(this);
            else if (accion == acciones[1]) await _impresion.AbrirConfiguracionImpresorasAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo configurar la impresora.");
            await DisplayAlertAsync("Impresoras", ex.Message, "Aceptar");
        }
    }

    private async Task EnviarAsync(Factura factura)
    {
        if (factura.Estado != "Emitida" || factura.Anulada)
        {
            await DisplayAlertAsync("Enviar factura", "Solo se pueden enviar facturas emitidas y vigentes.", "Aceptar");
            return;
        }
        var canales = new[] { "Correo Electrónico", "WhatsApp / Compartir", "Guardar como PDF" };
        var canal = await DisplayActionSheetAsync("Enviar factura", "Cancelar", null, canales);
        if (canal is null or "Cancelar") return;
        try
        {
            var path = await GenerarPdfAsync(factura);
            if (canal == canales[0])
            {
                var cliente = await _db.GetClienteByIdAsync(factura.ClienteId);
                var correo = cliente?.Correo;
                var adjuntos = new List<EmailAttachment> { new(path) };
                var email = new EmailMessage($"Factura {factura.NCF}", "Adjuntamos su factura.", string.IsNullOrWhiteSpace(correo) ? Array.Empty<string>() : new[] { correo }) { Attachments = adjuntos };
                await Email.Default.ComposeAsync(email);
            }
            else if (canal == canales[1])
            {
                await Share.Default.RequestAsync(new ShareFileRequest($"Factura {factura.NCF}", new ShareFile(path)));
            }
            else
            {
                await Share.Default.RequestAsync(new ShareFileRequest($"Guardar factura {factura.NCF}", new ShareFile(path)));
            }
            if (await DisplayAlertAsync("Confirmar envío", "¿Confirmas que el documento fue enviado al cliente?", "Sí", "No"))
            {
                await _db.MarcarFacturaEnviadaAsync(factura.Id, _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar."));
                await CargarAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo preparar el envío de la factura.");
            await DisplayAlertAsync("Enviar factura", ex is InvalidOperationException ? ex.Message : "No se pudo preparar el documento para enviar.", "Aceptar");
        }
    }

    private async Task MostrarPagoAsync(Factura factura)
    {
        var texto = string.IsNullOrWhiteSpace(factura.InstruccionPago) ? "No hay instrucciones de pago configuradas." : factura.InstruccionPago;
        await DisplayAlertAsync($"Pago de factura {factura.NCF}", $"Total: {factura.Total:C}\n\n{texto}", "Cerrar");
    }

    private async Task CopiarAsync(Factura factura)
    {
        var tipos = new[] { "Para facturar", "Para cotización", "Para nota de crédito" };
        var tipo = await DisplayActionSheetAsync("Copia — Seleccione tipo de documento", "Cancelar", null, tipos);
        if (tipo == tipos[0])
            await Navigation.PushAsync(ActivatorUtilities.CreateInstance<FacturaEditarPage>(_services, factura, false, true));
        else if (tipo == tipos[1])
        {
            var cotizacion = new Cotizacion
            {
                ClienteId = factura.ClienteId, ClienteNombre = factura.ClienteNombre, Fecha = DateTime.UtcNow,
                ValidezHasta = DateTime.Today.AddDays(15), Items = factura.Items, Subtotal = factura.Subtotal,
                Itbis = factura.Itbis, Total = factura.Total, Nota = factura.Nota,
                FirmaEmpresaRuta = factura.FirmaEmpresaRuta, FirmaClienteRuta = factura.FirmaClienteRuta
            };
            await Navigation.PushAsync(ActivatorUtilities.CreateInstance<CotizacionEditarPage>(_services, cotizacion, false, true));
        }
        else if (tipo == tipos[2])
            await Navigation.PushAsync(ActivatorUtilities.CreateInstance<NotaCreditoEditarPage>(_services, factura.ClienteId, factura.Items));
    }

    private async Task AnularAsync(Factura factura)
    {
        if (factura.Anulada) return;
        if (factura.Estado == "Guardada")
        {
            await DisplayAlertAsync("Anular factura", "Los borradores aún no emitidos no pueden anularse.", "Aceptar");
            return;
        }
        try
        {
            var motivo = await DisplayPromptAsync("Anular factura", $"Motivo de anulación para {factura.NCF} (se emitirá una nota B04):", "Continuar", "Cancelar", maxLength: 500);
            if (string.IsNullOrWhiteSpace(motivo)) return;
            var confirmar = await DisplayAlertAsync("Confirmar anulación", $"Se generará una nota de crédito B04 por {factura.Total:C} y la factura quedará anulada. ¿Continuar?", "Anular y emitir B04", "Cancelar");
            if (!confirmar) return;
            var nota = await _db.AnularFacturaAsync(factura.Id, _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar."), motivo);
            await DisplayAlertAsync("Facturación", $"Factura anulada. Nota de crédito emitida: {nota.NCF}.", "Aceptar");
            await CargarAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo anular la factura."); await DisplayAlertAsync("Error", ex.Message, "Aceptar"); }
    }

    private async void OnVolverClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("//modulo-0");
}
