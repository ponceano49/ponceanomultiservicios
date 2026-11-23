using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel.Communication;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class FacturaEditarPage : ContentPage, IDocumentoEditable
{
    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly LicenciaService _licencia;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FacturaEditarPage> _logger;
    private Factura? _original;
    private readonly Factura? _copia;
    private Factura? DocumentoBase => _original ?? _copia;
    private readonly bool _soloLectura;
    private readonly List<ItemFactura> _items = new();
    private decimal _impuestoPorcentaje = 18m;
    private bool _impuestoIncluido;
    private bool _recargoActivo;
    private decimal _recargoPorcentaje;
    private string _simboloMoneda = "RD$";
    private string _firmaEmpresaRuta = string.Empty;
    private string _firmaClienteRuta = string.Empty;
    private bool _busy;
    private bool _cargado;
    private string? _estadoInicial;

    public bool EstaOcupado => _busy || !_cargado;
    public string? ErrorGuardado => MensajeLabel.Text;
    public bool HayCambios => !_soloLectura && _estadoInicial is not null && ((_copia is not null && _original is null) || _estadoInicial != CapturarEstado());
    private string CapturarEstado() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Cliente = (ClientePicker.SelectedItem as Cliente)?.Id, Tipo = (TipoNcfPicker.SelectedItem as NcfUsuario)?.Tipo,
        EstadoPago = EstadoPagoPicker.SelectedItem?.ToString(), MetodoPago = MetodoPagoPicker.SelectedItem?.ToString(),
        Vence = VencimientoFechaPicker.Date, VencimientoEliminado = VencimientoEliminadoSwitch.IsToggled,
        Nota = NotaEditor.Text ?? string.Empty, MiFirma = MiFirmaCheck.IsChecked, FirmaCliente = FirmaClienteCheck.IsChecked,
        _firmaEmpresaRuta, _firmaClienteRuta, Items = _items
    });

    public FacturaEditarPage(DatabaseService db, AuthService auth, LicenciaService licencia, ILoggerFactory loggerFactory, ILogger<FacturaEditarPage> logger, Factura? factura = null, bool soloLectura = false, bool esCopia = false)
    {
        InitializeComponent(); _db = db; _auth = auth; _licencia = licencia; _loggerFactory = loggerFactory; _logger = logger; _original = esCopia ? null : factura; _copia = esCopia ? factura : null; _soloLectura = soloLectura;
        Title = factura == null || esCopia ? "Nueva factura" : soloLectura ? "Ver factura" : "Editar factura guardada";
        EstadoDocumentoLabel.Text = factura == null || esCopia ? "Nueva" : factura.EstadoCorto;
        FechaDocumentoLabel.Text = (esCopia ? DateTime.Now : factura?.Fecha.ToLocalTime() ?? DateTime.Now).ToString("dd/MM/yyyy");
        if (factura != null)
            _items.AddRange(factura.Items.Select(item => new ItemFactura { Id = esCopia ? Guid.NewGuid() : item.Id, ArticuloId = esCopia ? null : item.ArticuloId, Descripcion = item.Descripcion, Cantidad = item.Cantidad, PrecioUnitario = item.PrecioUnitario, SujetoImpuesto = item.SujetoImpuesto }));
        if (_items.Count == 0) _items.Add(new ItemFactura());
        ReconstruirItems();
        #if WINDOWS
        ImprimirYCerrarLayout.IsVisible = !_soloLectura;
#endif
        if (_soloLectura)
        {
            TipoNcfPicker.IsEnabled = false; ClientePicker.IsEnabled = false; ItemsContainer.IsEnabled = false; ItemsActionsLayout.IsEnabled = false;
            EstadoPagoPicker.IsEnabled = false; MetodoPagoPicker.IsEnabled = false; VencimientoFechaPicker.IsEnabled = false; CargarFirmaButton.IsEnabled = false;
            VencimientoEliminadoSwitch.IsEnabled = false; MiFirmaCheck.IsEnabled = false; FirmaClienteCheck.IsEnabled = false;
            CargarFirmaClienteButton.IsEnabled = false; SolicitarFirmaButton.IsEnabled = false;
            NotaEditor.IsEnabled = false; GuardarButton.IsVisible = false; BorradorButton.IsVisible = false;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_cargado) return;
        try
        {
            ClientePicker.ItemsSource = await _db.GetAllClientesAsync();
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.");
            var ajustes = await _db.ObtenerAjustesAsync(uid);
            _impuestoPorcentaje = ajustes.ImpuestoPorcentaje;
            _impuestoIncluido = ajustes.ImpuestoIncluido;
            _recargoActivo = ajustes.RecargoActivo;
            _recargoPorcentaje = ajustes.RecargoPorcentaje;
            _simboloMoneda = string.IsNullOrWhiteSpace(ajustes.SimboloMoneda) ? "RD$" : ajustes.SimboloMoneda;
            PagoInfoLabel.Text = ConstruirInstruccionPago(ajustes);
            PagoInfoLabel.IsVisible = !string.IsNullOrWhiteSpace(PagoInfoLabel.Text);
            EstadoPagoPicker.ItemsSource = new[] { "Pendiente", "Pagada" };
            EstadoPagoPicker.SelectedItem = DocumentoBase?.EstadoPago ?? "Pendiente";
            var metodosPago = new List<string>();
            if (ajustes.PagoEfectivo) metodosPago.Add("Efectivo");
            if (ajustes.PagoTransferencia) metodosPago.Add("Transferencia");
            if (ajustes.PagoCheque) metodosPago.Add("Cheque");
            if (ajustes.PagoTarjeta) metodosPago.Add("Tarjeta");
            if (ajustes.PagoCredito) metodosPago.Add("Crédito");
            if (ajustes.PagoOtro && !string.IsNullOrWhiteSpace(ajustes.MetodoOtro)) metodosPago.Add(ajustes.MetodoOtro);
            MetodoPagoPicker.ItemsSource = metodosPago;
            MetodoPagoPicker.SelectedItem = DocumentoBase?.MetodoPago ?? metodosPago.FirstOrDefault();
            VenceLabel.Text = ajustes.VenceEnDias > 0 ? $"Vencimiento predeterminado: {ajustes.VenceEnDias} días después de emisión." : "Sin vencimiento predeterminado; selecciona una fecha si aplica.";
            VencimientoEliminadoSwitch.IsToggled = DocumentoBase?.VencimientoEliminado ?? false;
            VencimientoFechaPicker.Date = DocumentoBase?.VenceFecha?.ToLocalTime().Date ?? DateTime.Today.AddDays(ajustes.VenceEnDias);
            VencimientoFechaPicker.IsEnabled = !_soloLectura && !VencimientoEliminadoSwitch.IsToggled;
            NotaEditor.Text = DocumentoBase?.Nota ?? ajustes.NotaPorDefecto;
            FirmaClienteCheck.IsChecked = DocumentoBase?.SolicitarFirmaCliente ?? false;
            _firmaEmpresaRuta = DocumentoBase?.FirmaEmpresaRuta ?? string.Empty;
            _firmaClienteRuta = DocumentoBase?.FirmaClienteRuta ?? string.Empty;
            MiFirmaCheck.IsChecked = !string.IsNullOrWhiteSpace(_firmaEmpresaRuta);
            FirmaPreview.Source = _firmaEmpresaRuta;
            FirmaPreview.IsVisible = MiFirmaCheck.IsChecked && !string.IsNullOrWhiteSpace(_firmaEmpresaRuta);
            FirmaClientePreview.Source = _firmaClienteRuta;
            FirmaClientePreview.IsVisible = FirmaClienteCheck.IsChecked && !string.IsNullOrWhiteSpace(_firmaClienteRuta);
            CargarFirmaButton.IsVisible = MiFirmaCheck.IsChecked;
            CargarFirmaClienteButton.IsVisible = FirmaClienteCheck.IsChecked;
            SolicitarFirmaButton.IsVisible = FirmaClienteCheck.IsChecked;
            ActualizarTotales();
            var configuracionesNcf = await _db.ObtenerConfiguracionNcfAsync(uid);
            var tipos = configuracionesNcf.Where(n => n.Activo && n.Tipo is "B01" or "B02" or "B14" or "B16").ToList();
            TipoNcfPicker.ItemsSource = tipos;
            TipoNcfPicker.ItemDisplayBinding = new Binding(nameof(NcfUsuario.Tipo));
            if (tipos.Count == 0)
            {
                GuardarButton.IsEnabled = false;
                MensajeLabel.Text = "No hay tipos de factura B01/B02 habilitados en ajustes. Puedes guardar un borrador, pero no emitir hasta habilitar uno.";
            }
            else
            {
                GuardarButton.IsEnabled = true;
                TipoNcfPicker.SelectedItem = DocumentoBase is not null ? tipos.FirstOrDefault(n => n.Tipo == DocumentoBase.TipoNCF) ?? tipos[0] : tipos[0];
            }
            if (DocumentoBase is not null)
                ClientePicker.SelectedItem = ((IEnumerable<Cliente>)ClientePicker.ItemsSource).FirstOrDefault(c => c.Id == DocumentoBase.ClienteId);
            await ActualizarPreviewNcfAsync();
            if (ClientePicker.ItemsSource is IEnumerable<Cliente> clientes && !clientes.Any())
                MensajeLabel.Text = "Primero registra un cliente para crear una factura.";
            ReconstruirItems();
            _cargado = true;
            _estadoInicial = CapturarEstado();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo preparar la factura."); MensajeLabel.Text = ex.Message; GuardarButton.IsEnabled = false; _estadoInicial ??= CapturarEstado(); _cargado = true; }
        finally { ContenidoDocumento.IsEnabled = true; }
    }

    private void OnClienteChanged(object? sender, EventArgs e) => RncEntry.Text = (ClientePicker.SelectedItem as Cliente)?.RncCedula ?? string.Empty;

    private async void OnTipoNcfChanged(object? sender, EventArgs e) => await ActualizarPreviewNcfAsync();

    private async Task ActualizarPreviewNcfAsync()
    {
        if (_original is { NCF.Length: > 0 }) { NcfEntry.Text = _original.NCF; return; }
        if (TipoNcfPicker.SelectedItem is not NcfUsuario configuracion) return;
        try { NcfEntry.Text = await _db.PrevisualizarNCFAsync(configuracion.UsuarioId, configuracion.Tipo); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo previsualizar NCF."); MensajeLabel.Text = ex.Message; }
    }

    private static string ConstruirInstruccionPago(AjustesUsuario ajustes)
    {
        var metodos = new List<string>();
        if (ajustes.PagoEfectivo) metodos.Add("Efectivo");
        if (ajustes.PagoTransferencia) metodos.Add("Transferencia");
        if (ajustes.PagoCheque) metodos.Add("Cheque");
        if (ajustes.PagoTarjeta) metodos.Add("Tarjeta");
        if (ajustes.PagoCredito) metodos.Add("Crédito");
        if (ajustes.PagoOtro && !string.IsNullOrWhiteSpace(ajustes.MetodoOtro)) metodos.Add(ajustes.MetodoOtro);
        var detalles = new List<string>();
        if (metodos.Count > 0) detalles.Add($"Métodos de pago: {string.Join(", ", metodos)}");
        if (ajustes.PagoTransferencia && !string.IsNullOrWhiteSpace(ajustes.BancoNombre))
            detalles.Add($"Banco: {ajustes.BancoNombre}; cuenta {ajustes.BancoTipoCuenta}: {ajustes.BancoNumeroCuenta}; RNC: {ajustes.BancoRnc}");
        if (ajustes.PagoTransferencia && !string.IsNullOrWhiteSpace(ajustes.MensajeReferencia)) detalles.Add(ajustes.MensajeReferencia);
        if (!string.IsNullOrWhiteSpace(ajustes.InstruccionPago)) detalles.Add(ajustes.InstruccionPago);
        return string.Join(Environment.NewLine, detalles);
    }

    private void OnAgregarItemClicked(object? sender, EventArgs e)
    {
        if (_soloLectura) return;
        _items.Add(new ItemFactura());
        ReconstruirItems();
    }

    private async void OnAgregarArticuloClicked(object? sender, EventArgs e)
    {
        if (_soloLectura) return;
        try
        {
            var articulos = await _db.GetArticulosAsync();
            var opciones = articulos.Select(a => a.Descripcion).Append("+ Crear artículo nuevo").ToArray();
            var seleccion = await DisplayActionSheetAsync("Selecciona un artículo", "Cancelar", null, opciones);
            if (seleccion is null or "Cancelar") return;
            ItemFactura? nuevo = null;
            var articulo = articulos.FirstOrDefault(a => a.Descripcion == seleccion);
            if (articulo is null)
                nuevo = await ArticuloRapidoPage.MostrarAsync(Navigation, _db, _logger, string.Empty);
            if (articulo is null && nuevo is null) return;
            var item = _items.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Descripcion)) ?? new ItemFactura();
            if (!_items.Contains(item)) _items.Add(item);
            if (articulo is not null)
            {
                item.ArticuloId = articulo.Id;
                item.Descripcion = articulo.Descripcion; item.PrecioUnitario = articulo.Precio; item.Cantidad = Math.Max(1, item.Cantidad);
                item.SujetoImpuesto = articulo.Itbis > 0;
            }
            else if (nuevo is not null)
            {
                item.ArticuloId = nuevo.ArticuloId;
                item.Descripcion = nuevo.Descripcion; item.PrecioUnitario = nuevo.PrecioUnitario; item.Cantidad = nuevo.Cantidad;
                item.SujetoImpuesto = nuevo.SujetoImpuesto;
            }
            ReconstruirItems();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo seleccionar el artículo."); MensajeLabel.Text = "No se pudieron cargar los artículos."; }
    }

    private async Task BuscarArticuloAsync(ItemFactura item, Entry descripcion, Entry cantidad, Entry precio, Func<string> obtenerProcesado, Action<string> guardarProcesado)
    {
        var nombre = descripcion.Text?.Trim() ?? string.Empty;
        if (nombre.Length == 0 || string.Equals(nombre, obtenerProcesado(), StringComparison.OrdinalIgnoreCase)) return;
        guardarProcesado(nombre);
        try
        {
            var articulo = (await _db.GetArticulosAsync()).FirstOrDefault(a => string.Equals(a.Descripcion, nombre, StringComparison.OrdinalIgnoreCase));
            if (articulo is not null)
            {
                item.ArticuloId = articulo.Id;
                item.Descripcion = articulo.Descripcion;
                item.PrecioUnitario = articulo.Precio;
                item.SujetoImpuesto = articulo.Itbis > 0;
                precio.Text = articulo.Precio.ToString(CultureInfo.CurrentCulture);
                ActualizarTotales();
                return;
            }
            var nuevo = await ArticuloRapidoPage.MostrarAsync(Navigation, _db, _logger, nombre);
            if (nuevo is null) return;
            item.Descripcion = nuevo.Descripcion;
            item.PrecioUnitario = nuevo.PrecioUnitario;
            item.Cantidad = nuevo.Cantidad;
            item.SujetoImpuesto = nuevo.SujetoImpuesto;
            guardarProcesado(nuevo.Descripcion);
            descripcion.Text = nuevo.Descripcion;
            item.ArticuloId = nuevo.ArticuloId;
            cantidad.Text = nuevo.Cantidad.ToString(CultureInfo.CurrentCulture);
            precio.Text = nuevo.PrecioUnitario.ToString(CultureInfo.CurrentCulture);
            ActualizarTotales();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo buscar o agregar el artículo del documento."); MensajeLabel.Text = "No se pudo buscar o agregar el artículo."; }
    }

    private void ReconstruirItems()
    {
        ItemsContainer.Children.Clear();
        foreach (var item in _items)
        {
            var descripcion = new Entry { Placeholder = "Nombre o descripción", Text = item.Descripcion, HorizontalOptions = LayoutOptions.Fill, FontSize = 12 };
            var cantidad = new Entry { Placeholder = "Cant.", Text = item.Cantidad.ToString(CultureInfo.CurrentCulture), Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.Center, FontSize = 12 };
            var precio = new Entry { Placeholder = "Precio", Text = item.PrecioUnitario.ToString(CultureInfo.CurrentCulture), Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.End, FontSize = 12 };
            var importe = new Label { Text = $"{_simboloMoneda} {item.Subtotal:N2}", FontSize = 12, TextColor = Color.FromArgb("#777777"), VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.End };
            var nombreProcesado = item.Descripcion;
            descripcion.TextChanged += (_, e) => { item.Descripcion = e.NewTextValue ?? string.Empty; item.ArticuloId = null; ActualizarTotales(); };
            descripcion.Completed += async (_, _) => await BuscarArticuloAsync(item, descripcion, cantidad, precio, () => nombreProcesado, nombre => nombreProcesado = nombre);
            descripcion.Unfocused += async (_, _) => await BuscarArticuloAsync(item, descripcion, cantidad, precio, () => nombreProcesado, nombre => nombreProcesado = nombre);
            cantidad.TextChanged += (_, e) => { item.Cantidad = int.TryParse(e.NewTextValue, out var value) ? value : 0; importe.Text = $"{_simboloMoneda} {item.Subtotal:N2}"; ActualizarTotales(); };
            precio.TextChanged += (_, e) => { item.PrecioUnitario = decimal.TryParse(e.NewTextValue, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ? value : -1m; importe.Text = $"{_simboloMoneda} {item.Subtotal:N2}"; ActualizarTotales(); };
            var quitar = new Button { Text = "×", TextColor = Colors.White, BackgroundColor = Color.FromArgb("#D9534F"), FontSize = 18, Padding = 0, MinimumHeightRequest = 32, MinimumWidthRequest = 32, HeightRequest = 32, WidthRequest = 32, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center, Command = new Command(() => { _items.Remove(item); if (_items.Count == 0) _items.Add(new ItemFactura()); ReconstruirItems(); }) };
            SemanticProperties.SetDescription(quitar, "Quitar artículo");
            var fila = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(75), new(110), new(110), new(55) }, Padding = new Thickness(8, 2), BackgroundColor = Color.FromArgb("#F7F7F7") };
            fila.Add(descripcion, 0); fila.Add(cantidad, 1); fila.Add(precio, 2); fila.Add(importe, 3); fila.Add(quitar, 4);
            ItemsContainer.Children.Add(fila);
        }
        ActualizarTotales();
    }

    private void ActualizarTotales()
    {
        var (subtotal, itbis, total) = CalculoDocumentoService.Calcular(_items, _impuestoPorcentaje, _impuestoIncluido);
        var recargo = _recargoActivo ? decimal.Round(total * _recargoPorcentaje / 100m, 2) : 0m;
        SubtotalLabel.Text = $"Subtotal: {_simboloMoneda} {subtotal:N2}";
        ItbisLabel.Text = $"ITBIS ({_impuestoPorcentaje:0.##}%): {_simboloMoneda} {itbis:N2}";
        RecargoLabel.IsVisible = _recargoActivo && _recargoPorcentaje > 0;
        RecargoLabel.Text = $"Recargo: {_recargoPorcentaje:0.##}% — {_simboloMoneda} {recargo:N2}";
        TotalLabel.Text = $"Total: {_simboloMoneda} {total + recargo:N2}";
    }

    private void OnTablaSizeChanged(object? sender, EventArgs e)
    {
        if (TablaScroll.Width > 0) TablaLayout.WidthRequest = Math.Max(650, TablaScroll.Width);
    }

    #if ANDROID
    private async void OnAccionesClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button) throw new InvalidOperationException("No se encontró el botón de acciones de la factura.");
        var accion = await AndroidMobileUi.ShowRowActionsAsync(button, "Acciones de la factura", new[] { "Copiar", "Pago", "PDF" });
        switch (accion)
        {
            case "Copiar": OnCopiarClicked(sender, e); break;
            case "Pago": OnPagoClicked(sender, e); break;
            case "PDF": OnVistaPreviaClicked(sender, e); break;
        }
    }

    private void OnEnviarPointerEntered(object? sender, PointerEventArgs e) { }
    private void OnEnviarPointerExited(object? sender, PointerEventArgs e) { }
    private async void OnEnviarClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button) throw new InvalidOperationException("No se encontró el botón de envío de la factura.");
        var accion = await AndroidMobileUi.ShowRowActionsAsync(button, "Enviar factura", new[] { "Correo electrónico", "WhatsApp / Compartir", "Guardar como PDF", "Convertir a cotización" });
        switch (accion)
        {
            case "Correo electrónico": OnEnviarCorreoClicked(sender, e); break;
            case "WhatsApp / Compartir":
            case "Guardar como PDF": OnEnviarCompartirClicked(sender, e); break;
            case "Convertir a cotización": OnConvertirMenuClicked(sender, e); break;
        }
    }
#else
    private void OnAccionesClicked(object? sender, EventArgs e) => AccionesDocumentoLayout.IsVisible = !AccionesDocumentoLayout.IsVisible;
    private void OnEnviarPointerEntered(object? sender, PointerEventArgs e) => EnviarPopup.IsVisible = true;
    private void OnEnviarPointerExited(object? sender, PointerEventArgs e) => EnviarPopup.IsVisible = false;
    private void OnEnviarClicked(object? sender, EventArgs e) => EnviarPopup.IsVisible = !EnviarPopup.IsVisible;
#endif
    private async void OnCopiarClicked(object? sender, EventArgs e) => await CopiarFacturaAsync();
    private async void OnVistaPreviaClicked(object? sender, EventArgs e) => await VerPdfAsync();
    private async void OnEnviarCorreoClicked(object? sender, EventArgs e)
    {
        EnviarPopup.IsVisible = false;
        if (!await ValidarFacturaParaEnviarAsync()) return;
        await EnviarFacturaAsync();
    }
    private async void OnEnviarCompartirClicked(object? sender, EventArgs e)
    {
        EnviarPopup.IsVisible = false;
        if (!await ValidarFacturaParaEnviarAsync()) return;
        try
        {
            var ruta = await CrearPdfActualAsync();
            await Share.Default.RequestAsync(new ShareFileRequest("Factura PDF", new ShareFile(ruta)));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo compartir el PDF de la factura."); MensajeLabel.Text = ex is InvalidOperationException ? ex.Message : "No se pudo preparar el PDF."; }
    }
    private async void OnConvertirMenuClicked(object? sender, EventArgs e)
    {
        EnviarPopup.IsVisible = false;
        await ConvertirACotizacionAsync();
    }
    private async Task<bool> ValidarFacturaParaEnviarAsync()
    {
        if (_original is { Estado: "Emitida", Anulada: false }) return true;
        await DisplayAlertAsync("Enviar factura", "Solo se pueden enviar facturas emitidas y vigentes.", "Aceptar");
        return false;
    }
    private async void OnPagoClicked(object? sender, EventArgs e)
    {
        var texto = string.IsNullOrWhiteSpace(PagoInfoLabel.Text) ? "No hay instrucciones de pago configuradas." : PagoInfoLabel.Text;
        await DisplayAlertAsync("Información de pago", texto, "Cerrar");
    }

    private void OnVencimientoEliminadoChanged(object? sender, ToggledEventArgs e) => VencimientoFechaPicker.IsEnabled = !_soloLectura && !e.Value;
    private void OnMiFirmaChanged(object? sender, CheckedChangedEventArgs e)
    {
        CargarFirmaButton.IsVisible = e.Value;
        FirmaPreview.IsVisible = e.Value && !string.IsNullOrWhiteSpace(_firmaEmpresaRuta);
    }

    private void OnFirmaClienteChanged(object? sender, CheckedChangedEventArgs e)
    {
        CargarFirmaClienteButton.IsVisible = e.Value;
        SolicitarFirmaButton.IsVisible = e.Value;
        FirmaClientePreview.IsVisible = e.Value && !string.IsNullOrWhiteSpace(_firmaClienteRuta);
    }

    private async Task<string?> CargarImagenFirmaAsync(string titulo)
    {
        var archivo = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = titulo, FileTypes = FilePickerFileType.Images });
        if (archivo is null) return null;
        var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.");
        var destino = Path.Combine(FileSystem.AppDataDirectory, $"firma-{uid}-{Guid.NewGuid():N}{Path.GetExtension(archivo.FileName)}");
        await using var origen = await archivo.OpenReadAsync();
        await using var salida = File.Create(destino);
        await origen.CopyToAsync(salida);
        return destino;
    }

    private async void OnCargarFirmaClicked(object? sender, EventArgs e)
    {
        try
        {
            var destino = await CargarImagenFirmaAsync("Selecciona la imagen de tu firma");
            if (destino is null) return;
            _firmaEmpresaRuta = destino;
            MiFirmaCheck.IsChecked = true;
            FirmaPreview.Source = destino;
            FirmaPreview.IsVisible = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo cargar la firma de la empresa.");
            MensajeLabel.Text = "No se pudo cargar la imagen de la firma.";
        }
    }

    private async void OnCargarFirmaClienteClicked(object? sender, EventArgs e)
    {
        try
        {
            var destino = await CargarImagenFirmaAsync("Selecciona la imagen de la firma del cliente");
            if (destino is null) return;
            _firmaClienteRuta = destino;
            FirmaClienteCheck.IsChecked = true;
            FirmaClientePreview.Source = destino;
            FirmaClientePreview.IsVisible = true;
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo cargar la firma del cliente."); MensajeLabel.Text = "No se pudo cargar la imagen de la firma del cliente."; }
    }

    private async void OnSolicitarFirmaClicked(object? sender, EventArgs e)
    {
        FirmaClienteCheck.IsChecked = true;
        await EnviarFacturaAsync("Solicitud de firma de factura");
    }

    private Factura CrearFacturaActual()
    {
        var cliente = ClientePicker.SelectedItem as Cliente ?? throw new InvalidOperationException("Selecciona un cliente antes de continuar.");
        var (subtotal, itbis, total) = CalculoDocumentoService.Calcular(_items.Where(i => !string.IsNullOrWhiteSpace(i.Descripcion)).ToList(), _impuestoPorcentaje, _impuestoIncluido);
        var recargo = _recargoActivo ? decimal.Round(total * _recargoPorcentaje / 100m, 2) : 0m;
        return new Factura
        {
            Id = _original?.Id ?? Guid.Empty, NCF = _original?.NCF ?? string.Empty,
            ClienteId = cliente.Id, ClienteNombre = cliente.Nombre, ClienteRnc = cliente.RncCedula,
            Fecha = _original?.Fecha ?? DateTime.UtcNow, Items = _items.Where(i => !string.IsNullOrWhiteSpace(i.Descripcion)).ToList(),
            Subtotal = subtotal, Itbis = itbis, Total = total + recargo, RecargoPorcentaje = _recargoPorcentaje, RecargoMonto = recargo,
            EstadoPago = EstadoPagoPicker.SelectedItem?.ToString() ?? "Pendiente", MetodoPago = MetodoPagoPicker.SelectedItem?.ToString() ?? string.Empty,
            VenceFecha = VencimientoEliminadoSwitch.IsToggled ? null : (VencimientoFechaPicker.Date ?? DateTime.Today).ToUniversalTime(),
            VencimientoEliminado = VencimientoEliminadoSwitch.IsToggled, Nota = NotaEditor.Text?.Trim() ?? string.Empty,
            FirmaEmpresaRuta = MiFirmaCheck.IsChecked ? _firmaEmpresaRuta : string.Empty,
            FirmaClienteRuta = FirmaClienteCheck.IsChecked ? _firmaClienteRuta : string.Empty,
            SolicitarFirmaCliente = FirmaClienteCheck.IsChecked,
            InstruccionPago = PagoInfoLabel.Text ?? string.Empty
        };
    }

    private async Task<string> CrearPdfActualAsync()
    {
        var factura = CrearFacturaActual();
        var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.");
        return await new DocumentoPdfService(_db).CrearFacturaAsync(factura, uid);
    }

    private async Task VerPdfAsync()
    {
        try
        {
            var path = await CrearPdfActualAsync();
            await Launcher.Default.OpenAsync(new OpenFileRequest("Factura PDF", new ReadOnlyFile(path)));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo generar el PDF de la factura actual."); MensajeLabel.Text = ex is InvalidOperationException ? ex.Message : "No se pudo generar el PDF."; }
    }

    private async Task CopiarFacturaAsync()
    {
        try
        {
            var tipos = new[] { "Para facturar", "Para cotización", "Para nota de crédito" };
            var tipo = await DisplayActionSheetAsync("Copia — Seleccione tipo de documento", "Cancelar", null, tipos);
            var factura = CrearFacturaActual();
            if (tipo == tipos[0])
                await Navigation.PushAsync(new FacturaEditarPage(_db, _auth, _licencia, _loggerFactory, _logger, factura, esCopia: true));
            else if (tipo == tipos[1])
            {
                var cotizacion = new Cotizacion
                {
                    ClienteId = factura.ClienteId, ClienteNombre = factura.ClienteNombre, Fecha = DateTime.UtcNow,
                    ValidezHasta = DateTime.Today.AddDays(15), Items = factura.Items, Subtotal = factura.Subtotal,
                    Itbis = factura.Itbis, Total = factura.Total, Nota = factura.Nota,
                    FirmaEmpresaRuta = factura.FirmaEmpresaRuta, FirmaClienteRuta = factura.FirmaClienteRuta
                };
                await Navigation.PushAsync(new CotizacionEditarPage(_db, _licencia, _auth, _loggerFactory.CreateLogger<CotizacionEditarPage>(), _loggerFactory, cotizacion, esCopia: true));
            }
            else if (tipo == tipos[2])
                await Navigation.PushAsync(new NotaCreditoEditarPage(_db, _auth, _loggerFactory.CreateLogger<NotaCreditoEditarPage>(), factura.ClienteId, factura.Items));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo iniciar la copia de la factura."); MensajeLabel.Text = "No se pudo iniciar la copia."; }
    }

    private async Task EnviarFacturaAsync(string asunto = "Factura")
    {
        try
        {
            var cliente = ClientePicker.SelectedItem as Cliente ?? throw new InvalidOperationException("Selecciona un cliente antes de enviar.");
            if (string.IsNullOrWhiteSpace(cliente.Correo)) throw new InvalidOperationException("El cliente no tiene correo registrado.");
            var path = await CrearPdfActualAsync();
            var mensaje = new EmailMessage
            {
                Subject = $"{asunto} {NcfEntry.Text}", Body = "Adjuntamos el documento. Responde a este correo con el documento firmado.",
                To = new List<string> { cliente.Correo }, Attachments = new List<EmailAttachment> { new(path) }
            };
            await Email.Default.ComposeAsync(mensaje);
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo preparar el correo de la factura."); MensajeLabel.Text = ex.Message; }
    }

    private async Task ConvertirACotizacionAsync()
    {
        if (_original is null || _original.NCF.Length == 0) { await DisplayAlertAsync("Convertir", "Guarda y emite primero la factura para convertirla en cotización.", "Aceptar"); return; }
        try
        {
            var cotizacion = await _db.ConvertirFacturaACotizacionAsync(_original.Id, _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar."));
            await DisplayAlertAsync("Cotización", $"Se creó la cotización para {cotizacion.ClienteNombre}.", "Aceptar");
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo convertir la factura a cotización."); MensajeLabel.Text = ex.Message; }
    }

    private async void OnGuardarClicked(object? sender, EventArgs e) => await GuardarAsync(true, imprimirYCerrar: ImprimirYCerrarCheck.IsChecked);
    private async void OnBorradorClicked(object? sender, EventArgs e) => await GuardarAsync(false);

    public Task<bool> GuardarAntesDeSalirAsync()
    {
        if (_soloLectura || (_original is not null && (_original.Estado != "Guardada" || _original.Anulada))) return Task.FromResult(true);
        return GuardarAsync(false, false, false);
    }

    private async Task<bool> GuardarAsync(bool emitir, bool cerrarAlGuardar = true, bool mostrarConfirmacion = true, bool imprimirYCerrar = false)
    {
        if (_busy) return false;
        var cliente = ClientePicker.SelectedItem as Cliente;
        var items = _items.Where(i => !string.IsNullOrWhiteSpace(i.Descripcion)).ToList();
        if (cliente == null) { MensajeLabel.Text = "Selecciona un cliente."; return false; }
        if (items.Count == 0 || items.Any(i => i.Cantidad <= 0 || i.PrecioUnitario < 0)) { MensajeLabel.Text = "Agrega un ítem con descripción, cantidad mayor que cero y precio válido."; return false; }
        if (MiFirmaCheck.IsChecked && string.IsNullOrWhiteSpace(_firmaEmpresaRuta)) { MensajeLabel.Text = "Carga la imagen de tu firma o desmarca la opción."; return false; }
        if (emitir && EstadoPagoPicker.SelectedItem?.ToString() == "Pagada" && MetodoPagoPicker.SelectedItem == null)
        { MensajeLabel.Text = "Selecciona el método de pago registrado para marcar esta factura como pagada."; return false; }
        var configuracionNcf = TipoNcfPicker.SelectedItem as NcfUsuario;
        var tipoNcf = configuracionNcf?.Tipo ?? string.Empty;
        if (emitir && (configuracionNcf == null || !System.Text.RegularExpressions.Regex.IsMatch(NcfEntry.Text ?? string.Empty, $"^{System.Text.RegularExpressions.Regex.Escape(configuracionNcf.Prefijo)}[0-9]{{8}}$"))) { MensajeLabel.Text = "No se pudo generar un NCF válido. Cancela y vuelve a intentarlo."; return false; }
        _busy = true; GuardarButton.IsEnabled = false; BorradorButton.IsEnabled = false;
        try
        {
            var (subtotal, itbis, total) = CalculoDocumentoService.Calcular(items, _impuestoPorcentaje, _impuestoIncluido);
            var factura = new Factura
            {
                Id = _original?.Id ?? Guid.Empty, NCF = _original?.NCF ?? string.Empty,
                TipoNCF = (TipoNcfPicker.SelectedItem as NcfUsuario)?.Tipo ?? _original?.TipoNCF ?? "B01", Estado = _original?.Estado ?? "Guardada",
                ClienteId = cliente.Id, ClienteNombre = cliente.Nombre, ClienteRnc = cliente.RncCedula,
                Items = items, Subtotal = subtotal, Itbis = itbis, Total = total + (_recargoActivo ? decimal.Round(total * _recargoPorcentaje / 100m, 2) : 0m),
                RecargoPorcentaje = _recargoActivo ? _recargoPorcentaje : 0m,
                RecargoMonto = _recargoActivo ? decimal.Round(total * _recargoPorcentaje / 100m, 2) : 0m,
                InstruccionPago = PagoInfoLabel.Text ?? string.Empty,
                EstadoPago = EstadoPagoPicker.SelectedItem?.ToString() ?? "Pendiente",
                MetodoPago = MetodoPagoPicker.SelectedItem?.ToString() ?? string.Empty,
                VenceFecha = VencimientoEliminadoSwitch.IsToggled ? null : (VencimientoFechaPicker.Date ?? DateTime.Today).ToUniversalTime(),
                VencimientoEliminado = VencimientoEliminadoSwitch.IsToggled,
                Nota = NotaEditor.Text?.Trim() ?? string.Empty,
                FirmaEmpresaRuta = MiFirmaCheck.IsChecked ? _firmaEmpresaRuta : string.Empty,
                FirmaClienteRuta = FirmaClienteCheck.IsChecked ? _firmaClienteRuta : string.Empty,
                SolicitarFirmaCliente = FirmaClienteCheck.IsChecked,
                UsuarioId = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.")
            };
            await _db.GuardarFacturaAsync(factura, emitir);
            _original = factura;
            _estadoInicial = CapturarEstado();
            if (mostrarConfirmacion) await DisplayAlertAsync("Facturación", emitir ? $"Factura {factura.NCF} emitida correctamente." : "Factura guardada como borrador.", "Aceptar");
            if (imprimirYCerrar && emitir)
            {
                try
                {
                    var rutaPdf = await new DocumentoPdfService(_db).CrearFacturaAsync(factura, factura.UsuarioId);
                    await new ImpresionWindowsService().AbrirVistaPreviaAsync(rutaPdf, $"Factura {factura.NCF}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "La factura {Ncf} se emitió, pero no se pudo abrir la vista previa de impresión.", factura.NCF);
                    MensajeLabel.Text = "La factura se emitió, pero no se pudo abrir la vista previa de impresión. Puedes reimprimirla desde Facturas.";
                    return true;
                }
            }
            if (cerrarAlGuardar) await Navigation.PopAsync();
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo guardar la factura."); MensajeLabel.Text = ex.Message; return false; }
        finally { _busy = false; GuardarButton.IsEnabled = TipoNcfPicker.SelectedItem is NcfUsuario; BorradorButton.IsEnabled = true; }
    }

    protected override bool OnBackButtonPressed() => _busy || base.OnBackButtonPressed();
    private async void OnCancelarClicked(object? sender, EventArgs e)
    {
        if (await DocumentoNavegacion.ConfirmarSalidaAsync(this, this)) await Navigation.PopAsync();
    }
}
