using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public sealed class SoportePage : ContentPage
{
    private const string CorreoSoporte = "ponceanomultiservicios@hotmail.com";
    private readonly ILogger<SoportePage> _logger;
    private readonly AuthService _auth;
    private readonly Picker _tipoAyudaPicker = new() { Title = "Selecciona una opción" };
    private readonly Entry _nombreEntry = new() { Placeholder = "Nombre" };
    private readonly Entry _correoEntry = new() { IsReadOnly = true, IsEnabled = false, Keyboard = Keyboard.Email };
    private readonly Entry _asuntoEntry = new() { Placeholder = "Tema" };
    private readonly Editor _mensajeEditor = new() { Placeholder = "Mensaje", AutoSize = EditorAutoSizeOption.TextChanges, HeightRequest = 140, MaxLength = 2000 };
    private readonly Label _contadorLabel = new() { Text = "0 / 2000", HorizontalTextAlignment = TextAlignment.End, FontSize = 12, TextColor = Colors.DimGray };
    private readonly Label _estadoLabel = new() { TextColor = Colors.DarkRed };
    private readonly Button _enviarButton = new() { Text = "ENVIAR", BackgroundColor = Color.FromArgb("#4C5AB8"), TextColor = Colors.White, CornerRadius = 4, Padding = new Thickness(22, 10), HorizontalOptions = LayoutOptions.Start };

    public SoportePage(ILogger<SoportePage> logger, AuthService auth)
    {
        _logger = logger;
        _auth = auth;
        Title = "Soporte";
        _nombreEntry.Text = auth.CurrentUser?.Nombre ?? string.Empty;
        _correoEntry.Text = CorreoSoporte;
        _tipoAyudaPicker.ItemsSource = new[] { "Reportar un problema", "Hacer una pregunta", "Consulta de facturación", "Otra consulta" };
        _tipoAyudaPicker.SelectedIndex = 0;
        _mensajeEditor.TextChanged += (_, e) => _contadorLabel.Text = $"{e.NewTextValue?.Length ?? 0} / 2000";
        _enviarButton.Clicked += async (_, _) => await EnviarAsync();

        var formulario = new VerticalStackLayout
        {
            Padding = new Thickness(12, 16),
            Spacing = 18,
            Children =
            {
                Campo("¿Cómo podemos ayudarte?", _tipoAyudaPicker),
                Campo("Nombre", _nombreEntry),
                Campo("Correo electrónico *", _correoEntry),
                Campo("Tema *", _asuntoEntry),
                new VerticalStackLayout { Spacing = 4, Children = { new Label { Text = "Mensaje *", FontSize = 12, TextColor = Colors.Gray }, _mensajeEditor, _contadorLabel } },
                _estadoLabel,
                _enviarButton
            }
        };

        Content = new Grid
        {
            Padding = new Thickness(18, 8),
            RowDefinitions = new RowDefinitionCollection { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            RowSpacing = 12,
            BackgroundColor = Colors.White,
            Children =
            {
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children = { new Label { Text = "◎", FontSize = 23, TextColor = Color.FromArgb("#687D90"), VerticalOptions = LayoutOptions.Center }, new Label { Text = "Soporte", FontSize = 24, TextColor = Color.FromArgb("#687D90"), VerticalOptions = LayoutOptions.Center } }
                },
                new Border
                {
                    Stroke = Color.FromArgb("#DCE1E6"),
                    StrokeThickness = 1,
                    Padding = 0,
                    Content = new ScrollView { Content = formulario }
                }
            }
        };
        Grid.SetRow((View)((Grid)Content).Children[1], 1);
    }

    private static View Campo(string titulo, View control) => new VerticalStackLayout
    {
        Spacing = 2,
        Children = { new Label { Text = titulo, FontSize = 12, TextColor = Colors.Gray }, control }
    };

    private async Task EnviarAsync()
    {
        if (_auth.CurrentUser is null)
        {
            _estadoLabel.Text = "Inicia sesión para contactar soporte.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_asuntoEntry.Text) || string.IsNullOrWhiteSpace(_mensajeEditor.Text))
        {
            _estadoLabel.Text = "Completa el tema y el mensaje.";
            return;
        }
        try
        {
            var usuario = _auth.CurrentUser;
            var asunto = $"[{_tipoAyudaPicker.SelectedItem}] {_asuntoEntry.Text.Trim()}";
            var cuerpo = $"Nombre: {_nombreEntry.Text?.Trim()}\nCorreo del usuario: {usuario.Correo}\n\n{_mensajeEditor.Text.Trim()}";
            var correo = new EmailMessage(asunto, cuerpo, CorreoSoporte);
            await Email.Default.ComposeAsync(correo);
            await DisplayAlertAsync("Soporte", "Se abrió tu aplicación de correo. Revisa y confirma el envío desde allí.", "Aceptar");
            _estadoLabel.Text = string.Empty;
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlertAsync("Soporte", "Este dispositivo no tiene una aplicación de correo compatible.", "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo preparar el correo de soporte.");
            await DisplayAlertAsync("Soporte", "No se pudo preparar el correo. Inténtalo nuevamente.", "Aceptar");
        }
    }
}
