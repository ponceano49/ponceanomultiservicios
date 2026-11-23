using System.Globalization;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public sealed class NcfConfiguracionPage : ContentPage
{
    private readonly DatabaseService _db;
    private readonly string _usuarioId;
    private readonly ILogger<NcfConfiguracionPage> _logger;
    private readonly VerticalStackLayout _filas = new() { Spacing = 12 };
    private readonly Label _mensaje = new() { TextColor = Colors.DarkRed };
    private readonly List<(NcfUsuario Config, CheckBox Activo, Entry Prefijo, Entry Siguiente, Entry Final)> _controles = new();

    public NcfConfiguracionPage(DatabaseService db, string usuarioId, ILogger<NcfConfiguracionPage> logger)
    {
        _db = db; _usuarioId = usuarioId; _logger = logger;
        Title = "Comprobantes fiscales";
        var contenido = new VerticalStackLayout { Padding = 18, Spacing = 12 };
        contenido.Children.Add(new Label { Text = "Registrar / Editar Comprobantes Fiscales", FontSize = 22, FontAttributes = FontAttributes.Bold });
        contenido.Children.Add(new Label { Text = "Configura solo rangos autorizados a tu empresa por la DGII. El sistema guarda estos datos localmente y no autoriza comprobantes.", TextColor = Colors.DarkOrange });
        var encabezado = new Grid { ColumnDefinitions = Columns(), ColumnSpacing = 4 };
        encabezado.Add(Encabezado("Tipo NCF"), 0);
        encabezado.Add(Encabezado("Activo"), 1);
        encabezado.Add(Encabezado("Prefijo"), 2);
        encabezado.Add(Encabezado("Siguiente número"), 3);
        encabezado.Add(Encabezado("Número final"), 4);
        contenido.Children.Add(encabezado);
        contenido.Children.Add(_filas);
        contenido.Children.Add(_mensaje);
        contenido.Children.Add(new HorizontalStackLayout
        {
            Spacing = 10,
            Children =
            {
                new Button { Text = "CANCELAR", Command = new Command(async () => await CerrarAsync()) },
                new Button { Text = "GUARDAR COMPROBANTES", Command = new Command(async () => await GuardarAsync()) }
            }
        });
        Content = new ScrollView { Content = contenido };
    }

    protected override async void OnAppearing() { base.OnAppearing(); await CargarAsync(); }

    private async Task CargarAsync()
    {
        try
        {
            var configuraciones = await _db.ObtenerConfiguracionNcfAsync(_usuarioId);
            _filas.Children.Clear(); _controles.Clear();
            foreach (var config in configuraciones)
            {
                var activo = new CheckBox { IsChecked = config.Activo, HorizontalOptions = LayoutOptions.Center };
                var prefijo = new Entry { Text = config.Prefijo, Placeholder = "B01-", WidthRequest = 80 };
                var siguiente = new Entry { Text = config.SiguienteNumero.ToString(CultureInfo.InvariantCulture), Keyboard = Keyboard.Numeric, WidthRequest = 100 };
                var final = new Entry { Text = config.NumeroFinal.ToString(CultureInfo.InvariantCulture), Keyboard = Keyboard.Numeric, WidthRequest = 100 };
                var fila = new Grid { ColumnDefinitions = Columns(), ColumnSpacing = 4, Padding = new Thickness(0, 4) };
                fila.Add(new Label { Text = $"{config.Tipo}\n{config.Descripcion}", FontSize = 12, VerticalOptions = LayoutOptions.Center }, 0);
                fila.Add(activo, 1); fila.Add(prefijo, 2); fila.Add(siguiente, 3); fila.Add(final, 4);
                _filas.Children.Add(fila);
                _controles.Add((config, activo, prefijo, siguiente, final));
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo cargar la configuración NCF."); _mensaje.Text = "No se pudieron cargar los comprobantes fiscales."; }
    }

    private async Task GuardarAsync()
    {
        try
        {
            foreach (var (config, activo, prefijo, siguiente, final) in _controles)
            {
                if (!int.TryParse(siguiente.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var siguienteNumero) ||
                    !int.TryParse(final.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var numeroFinal))
                    throw new InvalidOperationException($"Los números de la secuencia {config.Tipo} deben ser enteros positivos.");
                var prefix = prefijo.Text?.Trim() ?? string.Empty;
                if (prefix != config.Tipo && prefix != config.Tipo + "-")
                    throw new InvalidOperationException($"El prefijo de {config.Tipo} debe ser {config.Tipo} o {config.Tipo}-. ");
                config.Activo = activo.IsChecked;
                config.Prefijo = prefix;
                config.SiguienteNumero = siguienteNumero;
                config.NumeroFinal = numeroFinal;
            }
            await _db.GuardarConfiguracionNcfAsync(_usuarioId, _controles.Select(control => control.Config).ToList());
            await DisplayAlertAsync("Comprobantes fiscales", "La configuración se guardó correctamente.", "Aceptar");
            await CerrarAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _mensaje.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la configuración NCF.");
            _mensaje.Text = "No se pudo guardar la configuración de comprobantes fiscales.";
        }
    }

    private async Task CerrarAsync()
    {
        if (Navigation.ModalStack.Count > 0) await Navigation.PopModalAsync();
    }

    private static ColumnDefinitionCollection Columns() => new()
    {
        new ColumnDefinition(new GridLength(1.3, GridUnitType.Star)),
        new ColumnDefinition(new GridLength(45)),
        new ColumnDefinition(new GridLength(80)),
        new ColumnDefinition(new GridLength(100)),
        new ColumnDefinition(new GridLength(100))
    };

    private static Label Encabezado(string texto) => new()
    {
        Text = texto, FontSize = 11, FontAttributes = FontAttributes.Bold,
        VerticalOptions = LayoutOptions.Center
    };
}
