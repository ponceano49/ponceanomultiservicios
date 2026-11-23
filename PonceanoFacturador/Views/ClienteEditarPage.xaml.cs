using System.Net.Mail;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class ClienteEditarPage : ContentPage
{
    private readonly DatabaseService _db;
    private readonly RncContribuyentesService _rncLocal;
    private readonly ILogger<ClienteEditarPage> _logger;
    private readonly Cliente? _cliente;
    private bool _busy;

    public ClienteEditarPage(DatabaseService db, RncContribuyentesService rncLocal, ILogger<ClienteEditarPage> logger, Cliente? cliente = null)
    {
        InitializeComponent();
        _db = db;
        _rncLocal = rncLocal;
        _logger = logger;
        _cliente = cliente;
        TituloLabel.Text = cliente == null ? "Nuevo cliente" : "Editar cliente";
        if (cliente != null)
        {
            NombreEntry.Text = cliente.Nombre;
            RncEntry.Text = cliente.RncCedula;
            DireccionEntry.Text = cliente.Direccion;
            TelefonoEntry.Text = cliente.Telefono;
            CorreoEntry.Text = cliente.Correo;
            ContribuyenteCheck.IsChecked = cliente.EsContribuyente;
        }
    }

    private async void OnVerificarRncClicked(object? sender, EventArgs e)
    {
        try
        {
            var rnc = ListaLocalRncService.NormalizarRnc(RncEntry.Text);
            if (!ListaLocalRncService.EsRncValido(rnc)) throw new InvalidOperationException("Introduce un RNC de 9 dígitos o una cédula de 11 dígitos.");
            var razonSocial = await _rncLocal.BuscarPorRncAsync(rnc);
            VerificacionRncLabel.Text = razonSocial is null
                ? "⚠️ RNC no encontrado en el padrón local. La consulta en línea DGII no está disponible."
                : $"✅ Encontrado en el padrón local: {razonSocial}. No equivale a una certificación de estado activo.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo verificar el RNC del cliente en el padrón local.");
            VerificacionRncLabel.Text = ex is InvalidOperationException ? ex.Message : "No se pudo consultar el padrón local de RNC.";
        }
    }

    private async void OnGuardarClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        var nombre = NombreEntry.Text?.Trim() ?? string.Empty;
        var rnc = RncEntry.Text?.Trim() ?? string.Empty;
        var direccion = DireccionEntry.Text?.Trim() ?? string.Empty;
        var telefono = TelefonoEntry.Text?.Trim() ?? string.Empty;
        var correo = CorreoEntry.Text?.Trim() ?? string.Empty;

        if (nombre.Length == 0 || nombre.Length > 150)
            MensajeLabel.Text = "El nombre es obligatorio y no puede superar 150 caracteres.";
        else if (rnc.Length < 9 || rnc.Length > 11 || rnc.Any(c => c < '0' || c > '9'))
            MensajeLabel.Text = "El RNC o cédula debe tener entre 9 y 11 dígitos.";
        else if (direccion.Length == 0 || direccion.Length > 250)
            MensajeLabel.Text = "La dirección es obligatoria y no puede superar 250 caracteres.";
        else if (telefono.Length == 0)
            MensajeLabel.Text = "El teléfono es obligatorio.";
        else if (correo.Length > 0 && (!MailAddress.TryCreate(correo, out var address) || address.Address != correo))
            MensajeLabel.Text = "Introduce un correo electrónico válido o deja el campo vacío.";
        else
        {
            _busy = true;
            GuardarButton.IsEnabled = CancelarButton.IsEnabled = false;
            try
            {
                var cliente = new Cliente
                {
                    Id = _cliente?.Id ?? Guid.Empty,
                    Nombre = nombre,
                    RncCedula = rnc,
                    Direccion = direccion,
                    Telefono = telefono,
                    Correo = correo,
                    EsContribuyente = ContribuyenteCheck.IsChecked
                };
                await _db.GuardarClienteAsync(cliente);
                await DisplayAlertAsync("Clientes", "Cliente guardado correctamente.", "Aceptar");
                await Navigation.PopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo guardar el cliente.");
                MensajeLabel.Text = ex is InvalidOperationException ? ex.Message : "No se pudo guardar el cliente. Inténtalo de nuevo.";
            }
            finally
            {
                _busy = false;
                GuardarButton.IsEnabled = CancelarButton.IsEnabled = true;
            }
        }
    }

    protected override bool OnBackButtonPressed() => _busy || base.OnBackButtonPressed();

    private async void OnCancelarClicked(object? sender, EventArgs e)
    {
        if (!_busy) await Navigation.PopAsync();
    }
}
