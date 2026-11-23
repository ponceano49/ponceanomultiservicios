using System.Globalization;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public sealed class NotasDebitoPage : ContentPage
{
    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly ILogger<NotasDebitoPage> _logger;
    private readonly Picker _factura = new() { Title = "Factura emitida relacionada" };
    private readonly Entry _monto = new() { Placeholder = "Monto antes de ITBIS", Keyboard = Keyboard.Numeric };
    private readonly Entry _itbis = new() { Placeholder = "ITBIS", Keyboard = Keyboard.Numeric, Text = "0.00" };
    private readonly Entry _motivo = new() { Placeholder = "Motivo del débito", MaxLength = 500 };
    private readonly Label _estado = new() { TextColor = Color.FromArgb("#60788F") };
    private readonly Button _emitir = new() { Text = "Emitir nota de débito B03", BackgroundColor = Color.FromArgb("#467FA6"), TextColor = Colors.White };
    private List<Factura> _facturas = new();
    private bool _busy;

    public NotasDebitoPage(DatabaseService db, AuthService auth, ILogger<NotasDebitoPage> logger)
    {
        _db = db;
        _auth = auth;
        _logger = logger;
        Title = "Notas de débito";
        _factura.ItemDisplayBinding = new Binding(nameof(Factura.NCF));
        _emitir.Clicked += OnEmitirClicked;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24, Spacing = 12, MaximumWidthRequest = 780,
                Children =
                {
                    new Label { Text = "Nota de débito", FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#667D91") },
                    new Label { Text = "La nota se emite como comprobante B03, se vincula a una factura vigente y se agrega al libro interno 607. No modifica inventario. Verifica los datos fiscales antes de emitir.", TextColor = Color.FromArgb("#60788F") },
                    _factura, _motivo, _monto, _itbis, _estado, _emitir
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para administrar notas de débito.");
            _facturas = (await _db.GetFacturasAsync(uid)).Where(factura => factura.Estado == "Emitida" && !factura.Anulada && !string.IsNullOrWhiteSpace(factura.NCF)).ToList();
            _factura.ItemsSource = _facturas;
            if (_facturas.Count > 0) _factura.SelectedIndex = 0;
            else _estado.Text = "No hay facturas emitidas disponibles para relacionar.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron cargar las facturas para notas de débito.");
            _estado.Text = "No se pudieron cargar las facturas relacionadas.";
        }
    }

    private async void OnEmitirClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (_factura.SelectedItem is not Factura factura) { _estado.Text = "Selecciona una factura emitida."; return; }
        if (string.IsNullOrWhiteSpace(_motivo.Text)) { _estado.Text = "El motivo es obligatorio."; return; }
        if (!decimal.TryParse(_monto.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var monto) || monto <= 0 ||
            !decimal.TryParse(_itbis.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var itbis) || itbis < 0)
        { _estado.Text = "El monto debe ser mayor que cero y el ITBIS no puede ser negativo."; return; }
        if (!await DisplayAlertAsync("Emitir nota de débito", $"Se emitirá un comprobante B03 asociado a {factura.NCF} por RD$ {monto + itbis:N2}. Una vez emitido no se puede editar.", "Emitir", "Cancelar")) return;
        _busy = true;
        _emitir.IsEnabled = false;
        try
        {
            var usuario = _auth.CurrentUser ?? throw new InvalidOperationException("Inicia sesión para continuar.");
            var nota = await _db.EmitirNotaDebitoAsync(new NotaDebito
            {
                IdNota = Guid.NewGuid(), IdFacturaOrigen = factura.Id, NCFAfectado = factura.NCF,
                RncComprador = factura.ClienteRnc, NombreComprador = factura.ClienteNombre,
                Motivo = _motivo.Text.Trim(), MontoFacturado = monto, Itbis = itbis,
                TotalMonto = monto + itbis, IdUsuario = usuario.IdFirebase
            });
            _estado.Text = $"Nota {nota.NCF} emitida y registrada en el libro interno 607.";
            _motivo.Text = _monto.Text = string.Empty;
            _itbis.Text = "0.00";
            await DisplayAlertAsync("Nota emitida", $"NCF: {nota.NCF}\nFactura afectada: {nota.NCFAfectado}\nTotal: RD$ {nota.TotalMonto:N2}", "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo emitir la nota de débito.");
            _estado.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _emitir.IsEnabled = true;
        }
    }
}
