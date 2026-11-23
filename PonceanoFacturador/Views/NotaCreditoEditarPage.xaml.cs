using System.Globalization;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class NotaCreditoEditarPage : ContentPage, IDocumentoEditable
{
    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly ILogger<NotaCreditoEditarPage> _logger;
    private readonly Guid? _clienteInicial;
    private NotaCredito? _nota;
    private readonly bool _soloLectura;
    private readonly Guid? _facturaInicial;
    private readonly string? _motivoInicial;
    private readonly bool _datosInicialesSinGuardar;
    private readonly List<ItemFactura> _items = new();
    private decimal _impuestoPorcentaje = 18m;
    private bool _impuestoIncluido;
    private string _simboloMoneda = "RD$";
    private bool _busy;
    private bool _cargado;
    private bool _consultandoFacturas;
    private int _versionFacturas;
    private Guid? _facturaNoDisponible;
    private string? _estadoInicial;

    public bool EstaOcupado => _busy || !_cargado || _consultandoFacturas;
    public string? ErrorGuardado => MensajeLabel.Text;
    public bool HayCambios => !_soloLectura && _estadoInicial is not null && ((_datosInicialesSinGuardar && _nota is null) || _estadoInicial != CapturarEstado());
    private string CapturarEstado() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Cliente = (ClientePicker.SelectedItem as Cliente)?.Id, Factura = (FacturaPicker.SelectedItem as FacturaPendiente)?.Factura.Id ?? _facturaNoDisponible,
        Motivo = MotivoPicker.SelectedItem?.ToString(), Items = _items
    });

    public NotaCreditoEditarPage(DatabaseService db, AuthService auth, ILogger<NotaCreditoEditarPage> logger, Guid? clienteId = null, IEnumerable<ItemFactura>? items = null, NotaCredito? nota = null, bool esCopia = false)
    {
        InitializeComponent(); _db = db; _auth = auth; _logger = logger; _clienteInicial = nota?.ClienteId ?? clienteId; _nota = esCopia ? null : nota;
        _soloLectura = nota is not null && !nota.EsBorrador && !esCopia;
        _facturaInicial = nota?.FacturaRelacionadaId;
        _motivoInicial = nota?.Motivo;
        _datosInicialesSinGuardar = esCopia || (nota is null && items is not null && items.Any());
        FechaLabel.Text = (nota?.Fecha.ToLocalTime() ?? DateTime.Now).ToString("dd/MM/yyyy");
        if (_soloLectura && nota is not null)
        {
            Title = "Detalle de nota de crédito";
            EstadoLabel.Text = nota.Estado;
            NcfLabel.Text = nota.NCF;
            ClientePicker.IsVisible = false;
            ClienteDetalleLabel.Text = nota.ClienteNombre;
            ClienteDetalleLabel.IsVisible = true;
            RncLabel.Text = nota.ClienteRnc;
            FacturaPicker.IsVisible = false;
            FacturaDetalleLabel.Text = nota.FacturaRelacionadaId is null ? "Sin factura relacionada" : "Factura relacionada";
            FacturaDetalleLabel.IsVisible = true;
            MotivoPicker.IsVisible = false;
            MotivoDetalleLabel.Text = nota.Motivo;
            MotivoDetalleLabel.IsVisible = true;
            ItemsActionsLayout.IsVisible = false;
            GuardarButton.IsVisible = false;
            BorradorButton.IsVisible = false;
            CopiarButton.IsVisible = true;
        }
        else if (nota is not null && !esCopia)
        {
            Title = "Editar borrador de nota de crédito";
            EstadoLabel.Text = "Borrador";
        }
        ClientePicker.ItemsSource = Array.Empty<Cliente>();
        MotivoPicker.ItemsSource = new[] { "Devolución", "Descuento", "Corrección de precio", "Otro" };
        MotivoPicker.SelectedIndex = 0;
        if (nota is not null) _items.AddRange(nota.Items.Select(item => new ItemFactura { Id = esCopia ? Guid.NewGuid() : item.Id, Descripcion = item.Descripcion, Cantidad = item.Cantidad, PrecioUnitario = item.PrecioUnitario, SujetoImpuesto = item.SujetoImpuesto }));
        else if (items is not null)
            _items.AddRange(items.Select(item => new ItemFactura
            {
                Descripcion = item.Descripcion, Cantidad = item.Cantidad,
                PrecioUnitario = item.PrecioUnitario, SujetoImpuesto = item.SujetoImpuesto
            }));
        if (_items.Count == 0) _items.Add(new ItemFactura());
        ReconstruirItems();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_cargado) return;
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.");
            if (_soloLectura && _nota is not null)
            {
                if (_nota.FacturaRelacionadaId is Guid facturaId)
                {
                    var factura = await _db.GetFacturaByIdAsync(facturaId, uid);
                    FacturaDetalleLabel.Text = factura is null ? "Factura relacionada no disponible" : $"Factura relacionada: {factura.NCF}";
                }
                _cargado = true;
                return;
            }
            var clientes = await _db.GetAllClientesAsync();
            ClientePicker.ItemsSource = clientes;
            if (_clienteInicial is Guid clienteId)
                ClientePicker.SelectedItem = clientes.FirstOrDefault(cliente => cliente.Id == clienteId);
            else if (clientes.Count > 0)
                ClientePicker.SelectedIndex = 0;
            if (_motivoInicial is not null && MotivoPicker.Items.Contains(_motivoInicial)) MotivoPicker.SelectedItem = _motivoInicial;
            var ajustes = await _db.ObtenerAjustesAsync(uid);
            _impuestoPorcentaje = ajustes.ImpuestoPorcentaje;
            _impuestoIncluido = ajustes.ImpuestoIncluido;
            _simboloMoneda = string.IsNullOrWhiteSpace(ajustes.SimboloMoneda) ? "RD$" : ajustes.SimboloMoneda;
            ActualizarTotales();
            await CargarFacturasAsync(_facturaInicial);
            if (_facturaInicial is Guid id && FacturaPicker.ItemsSource is IEnumerable<FacturaPendiente> facturas)
            {
                FacturaPicker.SelectedItem = facturas.FirstOrDefault(f => f.Factura.Id == id);
                if (FacturaPicker.SelectedItem is null)
                {
                    _facturaNoDisponible = id;
                    MensajeLabel.Text = "La factura relacionada ya no está disponible. El borrador conservará la referencia; selecciona otra factura para cambiarla.";
                }
            }
            if (clientes.Count == 0) MensajeLabel.Text = "Primero registra un cliente para crear una nota de crédito.";
            _cargado = true;
            _estadoInicial = CapturarEstado();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo preparar la nota de crédito."); MensajeLabel.Text = "No se pudieron cargar los datos necesarios."; GuardarButton.IsEnabled = false; _estadoInicial ??= CapturarEstado(); _cargado = true; }
        finally { ContenidoDocumento.IsEnabled = true; }
    }

    private async void OnClienteChanged(object? sender, EventArgs e)
    {
        if (_cargado)
        {
            _facturaNoDisponible = null;
            await CargarFacturasAsync();
        }
    }

    private void OnFacturaChanged(object? sender, EventArgs e)
    {
        if (_cargado && FacturaPicker.SelectedItem is not null) _facturaNoDisponible = null;
    }

    private async Task CargarFacturasAsync(Guid? mantenerFactura = null)
    {
        var version = ++_versionFacturas;
        FacturaPicker.ItemsSource = Array.Empty<FacturaPendiente>();
        if (ClientePicker.SelectedItem is not Cliente cliente) { _consultandoFacturas = false; RncLabel.Text = string.Empty; return; }
        RncLabel.Text = cliente.RncCedula;
        _consultandoFacturas = true;
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.");
            var facturas = await _db.GetFacturasPendientesAsync(cliente.Id, uid);
            var notas = await _db.GetNotasCreditoPorClienteAsync(cliente.Id, uid);
            if (mantenerFactura is Guid id && facturas.All(f => f.Id != id))
            {
                var factura = await _db.GetFacturaByIdAsync(id, uid);
                if (factura?.ClienteId == cliente.Id) facturas.Add(factura);
            }
            if (version != _versionFacturas) return;
            FacturaPicker.ItemsSource = facturas.Select(f => new FacturaPendiente(f,
                f.Total - notas.Where(n => n.FacturaRelacionadaId == f.Id && !n.Anulada && !n.EsBorrador).Sum(n => n.Total), _simboloMoneda)).ToList();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudieron cargar facturas pendientes."); MensajeLabel.Text = "No se pudieron consultar las facturas relacionadas."; }
        finally { if (version == _versionFacturas) _consultandoFacturas = false; }
    }

    private void OnAgregarItemClicked(object? sender, EventArgs e) { if (!_busy) { _items.Add(new ItemFactura()); ReconstruirItems(); } }

    private async void OnAgregarArticuloClicked(object? sender, EventArgs e)
    {
        try
        {
            var articulos = await _db.GetArticulosAsync();
            if (articulos.Count == 0) { await DisplayAlertAsync("Artículos", "Primero registra un artículo.", "Aceptar"); return; }
            var seleccion = await DisplayActionSheetAsync("Selecciona un artículo", "Cancelar", null, articulos.Select(a => a.Descripcion).ToArray());
            var articulo = articulos.FirstOrDefault(a => a.Descripcion == seleccion);
            if (articulo == null) return;
            var item = _items.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Descripcion)) ?? new ItemFactura();
            if (!_items.Contains(item)) _items.Add(item);
            item.Descripcion = articulo.Descripcion; item.PrecioUnitario = articulo.Precio; item.Cantidad = Math.Max(1, item.Cantidad);
            item.SujetoImpuesto = articulo.Itbis > 0;
            ReconstruirItems();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo agregar el artículo."); MensajeLabel.Text = "No se pudieron cargar los artículos."; }
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
            var fila = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(75), new(110), new(110), new(55) }, Padding = new Thickness(8, 2), BackgroundColor = Color.FromArgb("#F7F7F7") };
            var importe = new Label { Text = $"{_simboloMoneda} {item.Subtotal:N2}", FontSize = 12, TextColor = Color.FromArgb("#777777"), VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.End };
            if (_soloLectura)
            {
                fila.Add(new Label { Text = item.Descripcion, FontSize = 12, TextColor = Color.FromArgb("#777777"), VerticalOptions = LayoutOptions.Center }, 0);
                fila.Add(new Label { Text = item.Cantidad.ToString(CultureInfo.CurrentCulture), FontSize = 12, TextColor = Color.FromArgb("#777777"), VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.Center }, 1);
                fila.Add(new Label { Text = $"{_simboloMoneda} {item.PrecioUnitario:N2}", FontSize = 12, TextColor = Color.FromArgb("#777777"), VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.End }, 2);
            }
            else
            {
                var descripcion = new Entry { Placeholder = "Descripción", Text = item.Descripcion, FontSize = 12 };
                var cantidad = new Entry { Placeholder = "Cant.", Text = item.Cantidad.ToString(CultureInfo.CurrentCulture), Keyboard = Keyboard.Numeric, FontSize = 12, HorizontalTextAlignment = TextAlignment.Center };
                var precio = new Entry { Placeholder = "Precio", Text = item.PrecioUnitario.ToString(CultureInfo.CurrentCulture), Keyboard = Keyboard.Numeric, FontSize = 12, HorizontalTextAlignment = TextAlignment.End };
                descripcion.TextChanged += (_, e) => { item.Descripcion = e.NewTextValue ?? string.Empty; ActualizarTotales(); };
                cantidad.TextChanged += (_, e) => { item.Cantidad = int.TryParse(e.NewTextValue, out var value) ? value : 0; importe.Text = $"{_simboloMoneda} {item.Subtotal:N2}"; ActualizarTotales(); };
                precio.TextChanged += (_, e) => { item.PrecioUnitario = decimal.TryParse(e.NewTextValue, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ? value : -1m; importe.Text = $"{_simboloMoneda} {item.Subtotal:N2}"; ActualizarTotales(); };
                var quitar = new Button { Text = "×", TextColor = Colors.White, BackgroundColor = Color.FromArgb("#D9534F"), FontSize = 18, Padding = 0, MinimumHeightRequest = 32, MinimumWidthRequest = 32, HeightRequest = 32, WidthRequest = 32, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center, Command = new Command(() => { if (_busy) return; _items.Remove(item); if (_items.Count == 0) _items.Add(new ItemFactura()); ReconstruirItems(); }) };
                SemanticProperties.SetDescription(quitar, "Quitar artículo");
                fila.Add(descripcion, 0); fila.Add(cantidad, 1); fila.Add(precio, 2); fila.Add(quitar, 4);
            }
            fila.Add(importe, 3);
            ItemsContainer.Children.Add(fila);
        }
        ActualizarTotales();
    }

    private void ActualizarTotales()
    {
        try
        {
            var (subtotal, itbis, total) = !_soloLectura
                ? CalculoDocumentoService.Calcular(_items, _impuestoPorcentaje, _impuestoIncluido)
                : (_nota!.Subtotal, _nota.Itbis, _nota.Total);
            SubtotalLabel.Text = $"Subtotal: {_simboloMoneda} {subtotal:N2}";
            ItbisLabel.Text = !_soloLectura ? $"ITBIS ({_impuestoPorcentaje:0.##}%): {_simboloMoneda} {itbis:N2}" : $"ITBIS: {_simboloMoneda} {itbis:N2}";
            TotalLabel.Text = $"Total: {_simboloMoneda} {total:N2}";
        }
        catch (ArgumentOutOfRangeException) { MensajeLabel.Text = "El impuesto configurado no es válido."; }
    }

    private async void OnGuardarClicked(object? sender, EventArgs e)
    {
        if (EstaOcupado) return;
        var cliente = ClientePicker.SelectedItem as Cliente;
        var items = _items.Where(i => !string.IsNullOrWhiteSpace(i.Descripcion)).ToList();
        if (cliente == null) { MensajeLabel.Text = "Selecciona un cliente."; return; }
        if (MotivoPicker.SelectedItem is not string motivo) { MensajeLabel.Text = "Selecciona el motivo de la nota."; return; }
        if (items.Count == 0 || items.Any(i => i.Cantidad <= 0 || i.PrecioUnitario < 0)) { MensajeLabel.Text = "Agrega al menos una línea válida."; return; }
        _busy = true; GuardarButton.IsEnabled = false;
        try
        {
            var (subtotal, itbis, total) = CalculoDocumentoService.Calcular(items, _impuestoPorcentaje, _impuestoIncluido);
            var nota = new NotaCredito
            {
                Id = _nota?.Id ?? Guid.Empty,
                ClienteId = cliente.Id, ClienteNombre = cliente.Nombre, ClienteRnc = cliente.RncCedula,
                FacturaRelacionadaId = (FacturaPicker.SelectedItem as FacturaPendiente)?.Factura.Id ?? _facturaNoDisponible,
                Motivo = motivo, Items = items, Subtotal = subtotal, Itbis = itbis, Total = total,
                UsuarioId = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.")
            };
            await _db.GuardarNotaCreditoAsync(nota);
            _nota = nota;
            _estadoInicial = CapturarEstado();
            await DisplayAlertAsync("Nota de crédito", $"Nota {nota.NCF} emitida correctamente.", "Aceptar");
            await Navigation.PopAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo guardar la nota de crédito."); MensajeLabel.Text = ex.Message; }
        finally { _busy = false; GuardarButton.IsEnabled = true; }
    }

    private async void OnBorradorClicked(object? sender, EventArgs e)
    {
        if (EstaOcupado) return;
        if (await GuardarAntesDeSalirAsync())
        {
            await DisplayAlertAsync("Nota de crédito", "Borrador guardado sin emitir un NCF.", "Aceptar");
            await Navigation.PopAsync();
        }
        else await DisplayAlertAsync("Documento sin guardar", ErrorGuardado ?? "No se pudo guardar el borrador.", "Aceptar");
    }

    public async Task<bool> GuardarAntesDeSalirAsync()
    {
        if (_soloLectura) return true;
        if (_busy) return false;
        _busy = true;
        try
        {
            var cliente = ClientePicker.SelectedItem as Cliente;
            var (subtotal, itbis, total) = CalculoDocumentoService.Calcular(_items, _impuestoPorcentaje, _impuestoIncluido);
            var nota = new NotaCredito
            {
                Id = _nota?.Id ?? Guid.Empty, ClienteId = cliente?.Id ?? Guid.Empty,
                ClienteNombre = cliente?.Nombre ?? string.Empty, ClienteRnc = cliente?.RncCedula ?? string.Empty,
                FacturaRelacionadaId = (FacturaPicker.SelectedItem as FacturaPendiente)?.Factura.Id ?? _facturaNoDisponible,
                Motivo = MotivoPicker.SelectedItem?.ToString() ?? string.Empty, Items = _items.ToList(),
                Subtotal = subtotal, Itbis = itbis, Total = total,
                UsuarioId = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.")
            };
            await _db.GuardarBorradorNotaCreditoAsync(nota);
            _nota = nota;
            _estadoInicial = CapturarEstado();
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo guardar el borrador de nota de crédito."); MensajeLabel.Text = ex.Message; return false; }
        finally { _busy = false; }
    }

    private async void OnCopiarClicked(object? sender, EventArgs e)
    {
        if (_nota is null || _busy) return;
        try { await Navigation.PushAsync(new NotaCreditoEditarPage(_db, _auth, _logger, nota: _nota, esCopia: true)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo copiar la nota de crédito."); MensajeLabel.Text = "No se pudo crear la copia."; }
    }

    private async void OnCancelarClicked(object? sender, EventArgs e)
    {
        if (await DocumentoNavegacion.ConfirmarSalidaAsync(this, this)) await Navigation.PopAsync();
    }

    private sealed record FacturaPendiente(Factura Factura, decimal Saldo, string Simbolo)
    {
        public string Texto => $"{Factura.NCF} — {Factura.Fecha.ToLocalTime():dd/MM/yyyy} — saldo estimado {Simbolo} {Saldo:N2}";
    }
}
