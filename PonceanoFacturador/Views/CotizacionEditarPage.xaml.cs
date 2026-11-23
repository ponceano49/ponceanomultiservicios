using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel.Communication;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class CotizacionEditarPage : ContentPage, IDocumentoEditable
{
    private readonly DatabaseService _db;
    private readonly LicenciaService _licencia;
    private readonly AuthService _auth;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CotizacionEditarPage> _logger;
    private Cotizacion? _original;
    private readonly Cotizacion? _copia;
    private Cotizacion? DocumentoBase => _original ?? _copia;
    private readonly bool _soloLectura;
    private readonly List<ItemFactura> _items = new();
    private decimal _impuestoPorcentaje = 18m;
    private bool _impuestoIncluido;
    private string _firmaEmpresaRuta = string.Empty;
    private string _firmaClienteRuta = string.Empty;
    private bool _busy;
    private bool _cargado;
    private string? _estadoInicial;

    public bool EstaOcupado => _busy || !_cargado;
    public string? ErrorGuardado => MensajeLabel.Text;
    public bool HayCambios => !_soloLectura && _original?.ConvertidaAFactura != true && _estadoInicial is not null && ((_copia is not null && _original is null) || _estadoInicial != CapturarEstado());
    private string CapturarEstado() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Cliente = (ClientePicker.SelectedItem as Cliente)?.Id, Validez = ValidezPicker.Date,
        Nota = NotaEditor.Text ?? string.Empty, MiFirma = MiFirmaCheck.IsChecked, FirmaCliente = FirmaClienteCheck.IsChecked,
        _firmaEmpresaRuta, _firmaClienteRuta, Items = _items
    });

    public CotizacionEditarPage(DatabaseService db, LicenciaService licencia, AuthService auth, ILogger<CotizacionEditarPage> logger, ILoggerFactory loggerFactory, Cotizacion? cotizacion = null, bool soloLectura = false, bool esCopia = false)
    {
        InitializeComponent();
        _db = db; _licencia = licencia; _auth = auth; _logger = logger; _loggerFactory = loggerFactory; _original = esCopia ? null : cotizacion; _copia = esCopia ? cotizacion : null; _soloLectura = soloLectura;
        Title = cotizacion == null || esCopia ? "Nueva cotización" : soloLectura ? "Detalle de cotización" : "Editar cotización";
        EstadoDocumentoLabel.Text = cotizacion == null || esCopia ? "Nueva" : cotizacion.EstadoVisual;
        FechaDocumentoLabel.Text = (esCopia ? DateTime.Now : cotizacion?.Fecha.ToLocalTime() ?? DateTime.Now).ToString("dd/MM/yyyy");
        NumeroDocumentoLabel.Text = cotizacion is null || esCopia ? "Al guardar" : cotizacion.Id.ToString("N")[..8].ToUpperInvariant();
        ValidezPicker.Date = DateTime.Today.AddDays(15);
        ConvertirButton.IsVisible = cotizacion is { ConvertidaAFactura: false } || esCopia;
        ConvertirMenuOption.IsVisible = cotizacion?.ConvertidaAFactura != true;
        if (cotizacion != null)
        {
            ValidezPicker.Date = cotizacion.ValidezHasta.Date;
            NotaEditor.Text = cotizacion.Nota;
            _firmaEmpresaRuta = cotizacion.FirmaEmpresaRuta;
            _firmaClienteRuta = cotizacion.FirmaClienteRuta;
            MiFirmaCheck.IsChecked = !string.IsNullOrWhiteSpace(_firmaEmpresaRuta);
            FirmaClienteCheck.IsChecked = !string.IsNullOrWhiteSpace(_firmaClienteRuta);
            FirmaPreview.Source = _firmaEmpresaRuta;
            FirmaPreview.IsVisible = MiFirmaCheck.IsChecked;
            FirmaClientePreview.Source = _firmaClienteRuta;
            FirmaClientePreview.IsVisible = FirmaClienteCheck.IsChecked;
            CargarFirmaButton.IsVisible = MiFirmaCheck.IsChecked;
            CargarFirmaClienteButton.IsVisible = FirmaClienteCheck.IsChecked;
            _items.AddRange(cotizacion.Items.Select(item => new ItemFactura { Id = esCopia ? Guid.NewGuid() : item.Id, ArticuloId = esCopia ? null : item.ArticuloId, Descripcion = item.Descripcion, Cantidad = item.Cantidad, PrecioUnitario = item.PrecioUnitario, SujetoImpuesto = item.SujetoImpuesto }));
        }
        if (_items.Count == 0) _items.Add(new ItemFactura());
        ReconstruirItems();
        if (_soloLectura || (cotizacion?.ConvertidaAFactura == true && !esCopia))
        {
            ClientePicker.IsEnabled = false;
            ValidezPicker.IsEnabled = false;
            ItemsContainer.IsEnabled = false;
            ItemsActionsLayout.IsEnabled = false;
            MiFirmaCheck.IsEnabled = false; FirmaClienteCheck.IsEnabled = false;
            CargarFirmaButton.IsEnabled = false; CargarFirmaClienteButton.IsEnabled = false; NotaEditor.IsEnabled = false;
            GuardarButton.IsVisible = false;
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
            ActualizarTotales();
            if (DocumentoBase != null)
                ClientePicker.SelectedItem = ((IEnumerable<Cliente>)ClientePicker.ItemsSource).FirstOrDefault(c => c.Id == DocumentoBase.ClienteId);
            if (ClientePicker.ItemsSource is IEnumerable<Cliente> clientes && !clientes.Any())
                MensajeLabel.Text = "Primero registra un cliente para crear una cotización.";
            _cargado = true;
            _estadoInicial = CapturarEstado();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo cargar clientes."); MensajeLabel.Text = "No se pudieron cargar los clientes."; _estadoInicial ??= CapturarEstado(); _cargado = true; }
        finally { ContenidoDocumento.IsEnabled = true; }
    }

    private void OnAgregarItemClicked(object? sender, EventArgs e) { if (!_soloLectura) { _items.Add(new ItemFactura()); ReconstruirItems(); } }

    private async void OnAgregarArticuloClicked(object? sender, EventArgs e)
    {
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
            else _items.Remove(item);
            ReconstruirItems();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo seleccionar el artículo."); MensajeLabel.Text = "No se pudieron cargar los artículos."; }
    }

    private async Task BuscarArticuloAsync(ItemFactura item, Entry descripcion, Entry precio, Func<string> obtenerProcesado, Action<string> guardarProcesado)
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
            descripcion.Text = nuevo.Descripcion;
            item.ArticuloId = nuevo.ArticuloId;
            precio.Text = nuevo.PrecioUnitario.ToString(CultureInfo.CurrentCulture);
            ActualizarTotales();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo buscar o agregar el artículo del documento."); MensajeLabel.Text = "No se pudo buscar o agregar el artículo."; }
    }

    private void OnTablaSizeChanged(object? sender, EventArgs e)
    {
        if (TablaScroll.Width > 0) TablaLayout.WidthRequest = Math.Max(650, TablaScroll.Width);
    }

    private void ReconstruirItems()
    {
        ItemsContainer.Children.Clear();
        foreach (var item in _items)
        {
            var descripcion = new Entry { Placeholder = "Nombre o descripción", Text = item.Descripcion, HorizontalOptions = LayoutOptions.Fill, FontSize = 12 };
            var cantidad = new Entry { Placeholder = "Cant.", Text = item.Cantidad.ToString(CultureInfo.CurrentCulture), Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.Center, FontSize = 12 };
            var precio = new Entry { Placeholder = "Precio", Text = item.PrecioUnitario.ToString(CultureInfo.CurrentCulture), Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.End, FontSize = 12 };
            var importe = new Label { Text = $"RD$ {item.Subtotal:N2}", FontSize = 12, TextColor = Color.FromArgb("#777777"), VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.End };
            var nombreProcesado = item.Descripcion;
            descripcion.TextChanged += (_, e) => { item.Descripcion = e.NewTextValue ?? string.Empty; item.ArticuloId = null; ActualizarTotales(); };
            descripcion.Completed += async (_, _) => await BuscarArticuloAsync(item, descripcion, precio, () => nombreProcesado, nombre => nombreProcesado = nombre);
            descripcion.Unfocused += async (_, _) => await BuscarArticuloAsync(item, descripcion, precio, () => nombreProcesado, nombre => nombreProcesado = nombre);
            cantidad.TextChanged += (_, e) => { item.Cantidad = int.TryParse(e.NewTextValue, out var value) ? value : 0; importe.Text = $"RD$ {item.Subtotal:N2}"; ActualizarTotales(); };
            precio.TextChanged += (_, e) => { item.PrecioUnitario = decimal.TryParse(e.NewTextValue, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ? value : -1m; importe.Text = $"RD$ {item.Subtotal:N2}"; ActualizarTotales(); };
            var quitar = new Button { Text = "×", TextColor = Colors.White, BackgroundColor = Color.FromArgb("#D9534F"), FontSize = 18, Padding = 0, MinimumHeightRequest = 32, MinimumWidthRequest = 32, HeightRequest = 32, WidthRequest = 32, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center, Command = new Command(() => { _items.Remove(item); if (_items.Count == 0) _items.Add(new ItemFactura()); ReconstruirItems(); }) };
            SemanticProperties.SetDescription(quitar, "Quitar artículo");
            var fila = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(75), new(110), new(110), new(55) }, Padding = new Thickness(8, 2), BackgroundColor = Color.FromArgb("#F7F7F7") };
            fila.Add(descripcion, 0); fila.Add(cantidad, 1); fila.Add(precio, 2); fila.Add(importe, 3); fila.Add(quitar, 4);
            ItemsContainer.Children.Add(fila);
        }
        ActualizarTotales();
    }

    private decimal Subtotal => _items.Sum(i => i.Subtotal);
    private void ActualizarTotales()
    {
        var (subtotal, itbis, total) = CalculoDocumentoService.Calcular(_items, _impuestoPorcentaje, _impuestoIncluido);
        SubtotalLabel.Text = $"Subtotal: {subtotal:C}";
        ItbisLabel.Text = $"ITBIS ({_impuestoPorcentaje:0.##}%): {itbis:C}";
        TotalLabel.Text = $"Total: {total:C}";
    }

    private async void OnGuardarClicked(object? sender, EventArgs e) => await GuardarAsync(true, true);

    public Task<bool> GuardarAntesDeSalirAsync()
    {
        if (_soloLectura || _original?.ConvertidaAFactura == true) return Task.FromResult(true);
        return GuardarAsync(false, false);
    }

    private async Task<bool> GuardarAsync(bool cerrarAlGuardar, bool mostrarConfirmacion)
    {
        if (_busy || _soloLectura) return _soloLectura;
        var cliente = ClientePicker.SelectedItem as Cliente;
        var items = _items.Where(i => !string.IsNullOrWhiteSpace(i.Descripcion)).ToList();
        var fechaValidez = ValidezPicker.Date ?? DateTime.Today;
        if (cliente == null) { MensajeLabel.Text = "Selecciona un cliente."; return false; }
        if (items.Count == 0 || items.Any(i => i.Cantidad <= 0 || i.PrecioUnitario < 0)) { MensajeLabel.Text = "Agrega un ítem con descripción, cantidad mayor que cero y precio válido."; return false; }
        if (fechaValidez < DateTime.Today) { MensajeLabel.Text = "La fecha de validez no puede estar en el pasado."; return false; }
        _busy = true; GuardarButton.IsEnabled = false;
        try
        {
            var (subtotal, itbis, total) = CalculoDocumentoService.Calcular(items, _impuestoPorcentaje, _impuestoIncluido);
            var cotizacion = new Cotizacion
            {
                Id = _original?.Id ?? Guid.Empty, ClienteId = cliente.Id, ClienteNombre = cliente.Nombre,
                Fecha = _original?.Fecha ?? DateTime.UtcNow, ValidezHasta = fechaValidez,
                Items = items, Subtotal = subtotal, Itbis = itbis, Total = total,
                Enviada = _original?.Enviada ?? false, Pagada = _original?.Pagada ?? false,
                Nota = NotaEditor.Text?.Trim() ?? string.Empty,
                FirmaEmpresaRuta = MiFirmaCheck.IsChecked ? _firmaEmpresaRuta : string.Empty,
                FirmaClienteRuta = FirmaClienteCheck.IsChecked ? _firmaClienteRuta : string.Empty
            };
            await _db.GuardarCotizacionAsync(cotizacion, _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar."));
            _original = cotizacion;
            _estadoInicial = CapturarEstado();
            if (mostrarConfirmacion) await DisplayAlertAsync("Cotizaciones", "Cotización guardada correctamente.", "Aceptar");
            if (cerrarAlGuardar) await Navigation.PopAsync();
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo guardar la cotización."); MensajeLabel.Text = ex.Message; return false; }
        finally { _busy = false; GuardarButton.IsEnabled = true; }
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

    private void OnMiFirmaChanged(object? sender, CheckedChangedEventArgs e) { CargarFirmaButton.IsVisible = e.Value; FirmaPreview.IsVisible = e.Value && !string.IsNullOrWhiteSpace(_firmaEmpresaRuta); }
    private void OnFirmaClienteChanged(object? sender, CheckedChangedEventArgs e) { CargarFirmaClienteButton.IsVisible = e.Value; FirmaClientePreview.IsVisible = e.Value && !string.IsNullOrWhiteSpace(_firmaClienteRuta); }

    private async void OnCargarFirmaClicked(object? sender, EventArgs e)
    {
        try { var ruta = await CargarImagenFirmaAsync("Selecciona la imagen de tu firma"); if (ruta is null) return; _firmaEmpresaRuta = ruta; MiFirmaCheck.IsChecked = true; FirmaPreview.Source = ruta; FirmaPreview.IsVisible = true; }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo cargar la firma de la empresa."); MensajeLabel.Text = "No se pudo cargar la firma."; }
    }

    private async void OnCargarFirmaClienteClicked(object? sender, EventArgs e)
    {
        try { var ruta = await CargarImagenFirmaAsync("Selecciona la imagen de la firma del cliente"); if (ruta is null) return; _firmaClienteRuta = ruta; FirmaClienteCheck.IsChecked = true; FirmaClientePreview.Source = ruta; FirmaClientePreview.IsVisible = true; }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo cargar la firma del cliente."); MensajeLabel.Text = "No se pudo cargar la firma del cliente."; }
    }

    #if ANDROID
    private void OnMenuPointerEntered(object? sender, PointerEventArgs e) { }
    private void OnMenuPointerExited(object? sender, PointerEventArgs e) { }
    private async void OnMenuClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button) throw new InvalidOperationException("No se encontró el botón de acciones de la cotización.");
        var opciones = new List<string> { "Enviar por correo", "Compartir / Guardar PDF", "Ver PDF", "Copiar" };
        if (ConvertirMenuOption.IsVisible) opciones.Add("Convertir a factura");
        var accion = await AndroidMobileUi.ShowRowActionsAsync(button, "Acciones de la cotización", opciones);
        switch (accion)
        {
            case "Enviar por correo": OnEnviarCorreoClicked(sender, e); break;
            case "Compartir / Guardar PDF": OnEnviarCompartirClicked(sender, e); break;
            case "Ver PDF": OnVerPdfMenuClicked(sender, e); break;
            case "Copiar": OnCopiarMenuClicked(sender, e); break;
            case "Convertir a factura": OnConvertirMenuClicked(sender, e); break;
        }
    }
#else
    private void OnMenuPointerEntered(object? sender, PointerEventArgs e) => AccionesPopup.IsVisible = true;
    private void OnMenuPointerExited(object? sender, PointerEventArgs e) => AccionesPopup.IsVisible = false;
    private void OnMenuClicked(object? sender, EventArgs e) => AccionesPopup.IsVisible = !AccionesPopup.IsVisible;
#endif
    private async void OnEnviarCorreoClicked(object? sender, EventArgs e)
    {
        AccionesPopup.IsVisible = false;
        await EnviarCotizacionAsync();
    }
    private async void OnEnviarCompartirClicked(object? sender, EventArgs e)
    {
        AccionesPopup.IsVisible = false;
        try
        {
            var ruta = await CrearPdfAsync();
            await Share.Default.RequestAsync(new ShareFileRequest("Cotización PDF", new ShareFile(ruta)));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo compartir el PDF de la cotización."); MensajeLabel.Text = ex.Message; }
    }
    private async void OnVerPdfMenuClicked(object? sender, EventArgs e)
    {
        AccionesPopup.IsVisible = false;
        await VerPdfAsync();
    }
    private async void OnCopiarMenuClicked(object? sender, EventArgs e)
    {
        AccionesPopup.IsVisible = false;
        await CopiarCotizacionAsync();
    }
    private async void OnConvertirMenuClicked(object? sender, EventArgs e)
    {
        AccionesPopup.IsVisible = false;
        await OnConvertirAsync();
    }

    private Cotizacion CrearCotizacionActual()
    {
        var cliente = ClientePicker.SelectedItem as Cliente ?? throw new InvalidOperationException("Selecciona un cliente antes de continuar.");
        var (subtotal, itbis, total) = CalculoDocumentoService.Calcular(_items.Where(i => !string.IsNullOrWhiteSpace(i.Descripcion)).ToList(), _impuestoPorcentaje, _impuestoIncluido);
        return new Cotizacion
        {
            Id = _original?.Id ?? Guid.Empty, ClienteId = cliente.Id, ClienteNombre = cliente.Nombre,
            Fecha = _original?.Fecha ?? DateTime.UtcNow, ValidezHasta = ValidezPicker.Date ?? DateTime.Today,
            Items = _items.Where(i => !string.IsNullOrWhiteSpace(i.Descripcion)).ToList(), Subtotal = subtotal, Itbis = itbis, Total = total,
            Enviada = _original?.Enviada ?? false, Pagada = _original?.Pagada ?? false,
            Nota = NotaEditor.Text?.Trim() ?? string.Empty,
            FirmaEmpresaRuta = MiFirmaCheck.IsChecked ? _firmaEmpresaRuta : string.Empty,
            FirmaClienteRuta = FirmaClienteCheck.IsChecked ? _firmaClienteRuta : string.Empty
        };
    }

    private async Task<string> CrearPdfAsync()
    {
        var cotizacion = CrearCotizacionActual();
        var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.");
        return await new DocumentoPdfService(_db).CrearCotizacionAsync(cotizacion, uid);
    }

    private async Task VerPdfAsync()
    {
        try { var ruta = await CrearPdfAsync(); await Launcher.Default.OpenAsync(new OpenFileRequest("Cotización PDF", new ReadOnlyFile(ruta))); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo generar el PDF de la cotización."); MensajeLabel.Text = ex is InvalidOperationException ? ex.Message : "No se pudo generar el PDF."; }
    }

    private async Task CopiarCotizacionAsync()
    {
        try
        {
            var tipos = new[] { "Para cotización", "Para factura", "Para nota de crédito" };
            var tipo = await DisplayActionSheetAsync("Copia — Seleccione tipo de documento", "Cancelar", null, tipos);
            if (tipo == tipos[0])
                await Navigation.PushAsync(new CotizacionEditarPage(_db, _licencia, _auth, _logger, _loggerFactory, CrearCotizacionActual(), esCopia: true));
            else if (tipo == tipos[1])
            {
                var cotizacion = CrearCotizacionActual();
                var factura = new Factura
                {
                    ClienteId = cotizacion.ClienteId, ClienteNombre = cotizacion.ClienteNombre,
                    Fecha = DateTime.UtcNow, Items = cotizacion.Items, Subtotal = cotizacion.Subtotal,
                    Itbis = cotizacion.Itbis, Total = cotizacion.Total, Estado = "Guardada", Nota = cotizacion.Nota
                };
                await Navigation.PushAsync(new FacturaEditarPage(_db, _auth, _licencia, _loggerFactory, _loggerFactory.CreateLogger<FacturaEditarPage>(), factura, esCopia: true));
            }
            else if (tipo == tipos[2])
            {
                var cotizacion = CrearCotizacionActual();
                await Navigation.PushAsync(new NotaCreditoEditarPage(_db, _auth, _loggerFactory.CreateLogger<NotaCreditoEditarPage>(), cotizacion.ClienteId, cotizacion.Items));
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo iniciar la copia de la cotización."); MensajeLabel.Text = "No se pudo iniciar la copia."; }
    }

    private async Task EnviarCotizacionAsync()
    {
        try
        {
            var cliente = ClientePicker.SelectedItem as Cliente ?? throw new InvalidOperationException("Selecciona un cliente antes de enviar.");
            if (string.IsNullOrWhiteSpace(cliente.Correo)) throw new InvalidOperationException("El cliente no tiene correo registrado.");
            var ruta = await CrearPdfAsync();
            await Email.Default.ComposeAsync(new EmailMessage
            {
                Subject = "Cotización", Body = "Adjuntamos la cotización solicitada.",
                To = new List<string> { cliente.Correo }, Attachments = new List<EmailAttachment> { new(ruta) }
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo preparar el correo de cotización."); MensajeLabel.Text = ex.Message; }
    }

    private async void OnConvertirClicked(object? sender, EventArgs e) => await OnConvertirAsync();

    private async Task OnConvertirAsync()
    {
        if (_original is null || _busy) { await DisplayAlertAsync("Convertir", "Guarda primero la cotización.", "Aceptar"); return; }
        try
        {
            var factura = await _db.ConvertirCotizacionAFacturaAsync(_original.Id, _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar."));
            await DisplayAlertAsync("Factura creada", $"Se creó la factura {factura.NCF}.", "Aceptar");
            await Navigation.PopAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo convertir la cotización."); MensajeLabel.Text = ex.Message; }
    }

    private async void OnCancelarClicked(object? sender, EventArgs e)
    {
        if (await DocumentoNavegacion.ConfirmarSalidaAsync(this, this)) await Navigation.PopAsync();
    }
}
