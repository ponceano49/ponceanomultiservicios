using System.Globalization;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public sealed class ArticuloRapidoPage : ContentPage
{
    private readonly DatabaseService _db;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<ItemFactura?> _resultado = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Entry _nombre = new() { Placeholder = "Nombre *" };
    private readonly Entry _precio = new() { Placeholder = "Precio", Keyboard = Keyboard.Numeric, Text = "0" };
    private readonly CheckBox _sujetoImpuesto = new() { IsChecked = true };
    private readonly Entry _cantidad = new() { Placeholder = "Cantidad", Keyboard = Keyboard.Numeric, Text = "1" };
    private readonly Entry _descripcion = new() { Placeholder = "Descripción" };
    private readonly CheckBox _guardarCatalogo = new();
    private readonly Entry _stockInicial = new() { Placeholder = "Stock inicial", Keyboard = Keyboard.Numeric, Text = "0", IsVisible = false };
    private readonly Label _importe = new() { Text = "Importe: RD$ 0.00" };
    private readonly Label _mensaje = new() { TextColor = Colors.Red };
    private bool _cerrando;

    public ArticuloRapidoPage(DatabaseService db, ILogger logger, string nombreInicial)
    {
        _db = db;
        _logger = logger;
        _nombre.Text = nombreInicial;
        Title = "Agregar artículo";
        _precio.TextChanged += (_, _) => ActualizarImporte();
        _cantidad.TextChanged += (_, _) => ActualizarImporte();
        _guardarCatalogo.CheckedChanged += (_, e) => _stockInicial.IsVisible = e.Value;
        var acciones = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10 };
        acciones.Add(new Button { Text = "Cancelar", Command = new Command(async () => await CerrarAsync(null)) });
        var guardarBoton = new Button { Text = "Guardar", Command = new Command(async () => await GuardarAsync()) };
        Grid.SetColumn(guardarBoton, 1);
        acciones.Add(guardarBoton);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 12,
                Children =
                {
                    new Label { Text = "Artículo no encontrado en el catálogo", FontSize = 22, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "Completa los datos. Puedes usarlo solo en este documento o guardarlo para el futuro." },
                    _nombre,
                    _precio,
                    new HorizontalStackLayout { Children = { _sujetoImpuesto, new Label { Text = "Sujeto a impuesto", VerticalOptions = LayoutOptions.Center } } },
                    _cantidad,
                    _importe,
                    _descripcion,
                    new HorizontalStackLayout { Children = { _guardarCatalogo, new Label { Text = "Guardar artículo para uso futuro", VerticalOptions = LayoutOptions.Center } } },
                    _stockInicial,
                    _mensaje,
                    acciones
                }
            }
        };
    }

    public static async Task<ItemFactura?> MostrarAsync(INavigation navigation, DatabaseService db, ILogger logger, string nombre)
    {
        var page = new ArticuloRapidoPage(db, logger, nombre);
        await navigation.PushModalAsync(new NavigationPage(page));
        return await page._resultado.Task;
    }

    private void ActualizarImporte()
    {
        var precio = decimal.TryParse(_precio.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var p) ? p : 0m;
        var cantidad = decimal.TryParse(_cantidad.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var q) ? q : 0m;
        _importe.Text = $"Importe: RD$ {precio * cantidad:N2}";
    }

    private async Task GuardarAsync()
    {
        var nombre = _nombre.Text?.Trim() ?? string.Empty;
        if (nombre.Length == 0) { _mensaje.Text = "El nombre es obligatorio."; return; }
        if (!decimal.TryParse(_precio.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var precio) || precio < 0)
        { _mensaje.Text = "Introduce un precio válido, igual o mayor que cero."; return; }
        if (!int.TryParse(_cantidad.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var cantidad) || cantidad <= 0)
        { _mensaje.Text = "La cantidad debe ser un entero mayor que cero."; return; }
        try
        {
            if (_guardarCatalogo.IsChecked)
            {
                if (!int.TryParse(_stockInicial.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var stock) || stock < 0)
                { _mensaje.Text = "El stock inicial debe ser un entero no negativo."; return; }
                var articulo = new Articulo
                {
                    Codigo = nombre,
                    Descripcion = string.IsNullOrWhiteSpace(_descripcion.Text) ? nombre : _descripcion.Text.Trim(),
                    Precio = precio,
                    Itbis = _sujetoImpuesto.IsChecked ? 18m : 0m,
                    Stock = stock
                };
                await _db.GuardarArticuloAsync(articulo);
                await CerrarAsync(new ItemFactura
                {
                    ArticuloId = articulo.Id,
                    Descripcion = articulo.Descripcion,
                    PrecioUnitario = precio,
                    Cantidad = cantidad,
                    SujetoImpuesto = _sujetoImpuesto.IsChecked
                });
                return;
            }
            await CerrarAsync(new ItemFactura
            {
                Descripcion = string.IsNullOrWhiteSpace(_descripcion.Text) ? nombre : $"{nombre} — {_descripcion.Text.Trim()}",
                PrecioUnitario = precio,
                Cantidad = cantidad,
                SujetoImpuesto = _sujetoImpuesto.IsChecked
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar el artículo rápido.");
            _mensaje.Text = ex.Message;
        }
    }

    private async Task CerrarAsync(ItemFactura? resultado)
    {
        if (_cerrando) return;
        _cerrando = true;
        _resultado.TrySetResult(resultado);
        await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        MainThread.BeginInvokeOnMainThread(async () => await CerrarAsync(null));
        return true;
    }
}
