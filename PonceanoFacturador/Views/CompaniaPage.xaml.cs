using System.Net.Mail;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class CompaniaPage : ContentPage
{
    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly ILogger<CompaniaPage> _logger;
    private string _logoRuta = string.Empty;

    public CompaniaPage(DatabaseService db, AuthService auth, ILogger<CompaniaPage> logger)
    {
        InitializeComponent();
        _db = db;
        _auth = auth;
        _logger = logger;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para editar la compañía.");
            var config = await _db.ObtenerAjustesAsync(uid);
            NombreEntry.Text = config.NombreCompania;
            RncEntry.Text = config.RncCompania;
            CorreoEntry.Text = config.CorreoFactura;
            TelefonoEntry.Text = config.Telefonos;
            DireccionEntry.Text = config.Direccion1;
            DireccionAdicionalEntry.Text = config.Direccion2;
            _logoRuta = config.LogoRuta;
            LogoPreview.Source = _logoRuta;
            LogoPreview.IsVisible = !string.IsNullOrWhiteSpace(_logoRuta);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo cargar la información de la compañía.");
            MensajeLabel.Text = "No se pudo cargar la información de la compañía.";
        }
    }

    private void OnDatosPonceanoClicked(object? sender, EventArgs e)
    {
        NombreEntry.Text = "Ponceano Servicios, S.R.L.";
        RncEntry.Text = "1-32-58543-7";
        TelefonoEntry.Text = "1-849-531-3660 / 1-829-665-4316";
        CorreoEntry.Text = "Ponceeanomultiservicios@gmail.com";
        MensajeLabel.TextColor = Colors.DarkGreen;
        MensajeLabel.Text = "Datos cargados. Revisa la información, carga el logo y pulsa Guardar.";
    }

    private async void OnLogoClicked(object? sender, EventArgs e)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Selecciona el logo de la compañía", FileTypes = FilePickerFileType.Images });
            if (file is null) return;
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.");
            var destination = Path.Combine(FileSystem.AppDataDirectory, $"logo-{uid}.img");
            await using var source = await file.OpenReadAsync();
            await using var target = File.Create(destination);
            await source.CopyToAsync(target);
            _logoRuta = destination;
            LogoPreview.Source = destination;
            LogoPreview.IsVisible = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo cargar el logo de la compañía.");
            MensajeLabel.Text = "No se pudo cargar el logo.";
        }
    }

    private async void OnGuardarClicked(object? sender, EventArgs e)
    {
        var nombre = NombreEntry.Text?.Trim() ?? string.Empty;
        var correo = CorreoEntry.Text?.Trim() ?? string.Empty;
        if (nombre.Length == 0) { MensajeLabel.Text = "El nombre de la empresa es obligatorio."; return; }
        if (correo.Length > 0 && !MailAddress.TryCreate(correo, out _)) { MensajeLabel.Text = "Introduce un correo válido."; return; }
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para guardar.");
            var config = await _db.ObtenerAjustesAsync(uid);
            config.NombreCompania = nombre;
            config.RncCompania = RncEntry.Text?.Trim() ?? string.Empty;
            config.CorreoFactura = correo;
            config.Telefonos = TelefonoEntry.Text?.Trim() ?? string.Empty;
            config.Direccion1 = DireccionEntry.Text?.Trim() ?? string.Empty;
            config.Direccion2 = DireccionAdicionalEntry.Text?.Trim() ?? string.Empty;
            config.LogoRuta = _logoRuta;
            await _db.GuardarAjustesAsync(config);
            MensajeLabel.TextColor = Colors.DarkGreen;
            MensajeLabel.Text = "Información guardada correctamente.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la información de la compañía.");
            MensajeLabel.Text = "No se pudo guardar la información de la compañía.";
        }
    }

    private async void OnCancelarClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PopAsync(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo cerrar la información de la compañía.");
            MensajeLabel.Text = "No se pudo cerrar esta pantalla.";
        }
    }
}
