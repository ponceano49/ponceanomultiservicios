namespace PonceanoFacturador.Views;

public sealed class DialogoPermisoAdministrador : ContentPage
{
    private readonly TaskCompletionSource<string?> _resultado = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Entry _passwordEntry;
    private readonly Label _mensajeError;

    public Task<string?> Resultado => _resultado.Task;

    public DialogoPermisoAdministrador()
    {
        Title = "Permiso de administrador";
        BackgroundColor = Color.FromArgb("#F4F4F4");
        _passwordEntry = new Entry
        {
            Placeholder = "Contraseña de administrador",
            IsPassword = true,
            ReturnType = ReturnType.Done
        };
        _passwordEntry.Completed += (_, _) => Confirmar();
        _mensajeError = new Label { TextColor = Colors.DarkRed, IsVisible = false };

        var botones = new HorizontalStackLayout
        {
            Spacing = 12,
            HorizontalOptions = LayoutOptions.End,
            Children =
            {
                new Button { Text = "Cancelar", Command = new Command(Cancelar) },
                new Button { Text = "Desbloquear", Command = new Command(Confirmar) }
            }
        };

        var contenido = new VerticalStackLayout
        {
            Spacing = 14,
            Padding = 24,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label
                {
                    Text = "Esta función requiere permiso de administrador. Ingrese la contraseña",
                    FontSize = 18,
                    FontAttributes = FontAttributes.Bold
                },
                _passwordEntry,
                _mensajeError,
                botones
            }
        };
        Content = new Grid
        {
            Padding = 20,
            Children = { new Border { BackgroundColor = Colors.White, Stroke = Color.FromArgb("#DDDDDD"), Content = contenido } }
        };
    }

    private void Confirmar()
    {
        var password = _passwordEntry.Text;
        if (string.IsNullOrWhiteSpace(password))
        {
            _mensajeError.Text = "Ingrese la contraseña de administrador.";
            _mensajeError.IsVisible = true;
            return;
        }

        _passwordEntry.Text = string.Empty;
        _resultado.TrySetResult(password);
    }

    private void Cancelar() => _resultado.TrySetResult(null);

    protected override void OnDisappearing()
    {
        _passwordEntry.Text = string.Empty;
        _resultado.TrySetResult(null);
        base.OnDisappearing();
    }
}
