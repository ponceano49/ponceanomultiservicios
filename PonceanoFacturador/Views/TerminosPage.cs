namespace PonceanoFacturador.Views;

public sealed class TerminosPage : ContentPage
{
    private const string TextoTerminos = """
        TÉRMINOS Y CONDICIONES DE USO
        Ponceano Multiservicios — Facturación Electrónica

        1. INFORMACIÓN DEL PROPIETARIO
        Nombre: Wellington De Jesús Ponceano
        Empresa: Ponceano Multiservicios
        Correo: ponceanomultiservicios@hotmail.com
        País: República Dominicana

        2. DESCRIPCIÓN DEL SERVICIO
        Esta aplicación permite la gestión de facturas, clientes, comprobantes fiscales DGII y reportes contables. Existen dos modalidades de uso:
        - Versión Gratuita: límite de 10 facturas por mes
        - Versión Completa: sin límite de facturas, soporte prioritario

        3. CUENTAS Y DISPOSITIVOS
        - Cada cuenta puede activarse hasta en 2 dispositivos simultáneamente
        - Si necesita más dispositivos, el usuario debe solicitar la ampliación
        - El usuario es responsable de mantener la confidencialidad de sus credenciales

        4. DATOS Y PRIVACIDAD
        - Los datos de facturación se almacenan localmente en el dispositivo del usuario
        - La autenticación se gestiona a través de Firebase Authentication
        - No compartimos tus datos con terceros sin tu autorización
        - Es responsabilidad del usuario mantener copias de seguridad de su información

        5. COMPROBANTES FISCALES
        - La correcta emisión y conservación de comprobantes fiscales es responsabilidad exclusiva del usuario ante la DGII
        - Ponceano Multiservicios no se hace responsable por el uso incorrecto de números de NCF

        6. PROPIEDAD INTELECTUAL
        - Esta aplicación es propiedad de Wellington De Jesús Ponceano
        - No se permite su modificación, distribución o venta sin autorización expresa

        7. MODIFICACIONES
        - Nos reservamos el derecho de modificar estos términos cuando sea necesario
        - El uso continuado de la aplicación implica la aceptación de los cambios

        Última actualización: 15 de noviembre de 2026
        """;

    public TerminosPage()
    {
        Title = "Términos y Condiciones";
        var cerrarButton = new Button { Text = "Cerrar" };
        cerrarButton.Clicked += OnCerrarClicked;

        Content = new Grid
        {
            Padding = 20,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Star },
                new RowDefinition { Height = GridLength.Auto }
            },
            Children =
            {
                new ScrollView
                {
                    Content = new Label { Text = TextoTerminos, LineBreakMode = LineBreakMode.WordWrap }
                },
                cerrarButton
            }
        };
        Grid.SetRow(cerrarButton, 1);
    }

    private async void OnCerrarClicked(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }
}
