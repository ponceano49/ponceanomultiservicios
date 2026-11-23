namespace PonceanoFacturador.Views;

public sealed class ModuloPendientePage : ContentPage
{
    public ModuloPendientePage(string titulo, string descripcion)
    {
        Title = titulo;
        Content = new VerticalStackLayout
        {
            Padding = 32,
            Spacing = 16,
            Children =
            {
                new Label { Text = titulo, FontSize = 28, FontAttributes = FontAttributes.Bold },
                new Label { Text = descripcion, FontSize = 16 },
                new Label { Text = "Este módulo todavía no está disponible en esta versión.", TextColor = Colors.Gray }
            }
        };
    }
}
