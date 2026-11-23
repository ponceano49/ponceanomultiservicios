using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;
using PonceanoFacturador.Views;

namespace PonceanoFacturador;

public partial class MainPage : ContentPage
{
    private readonly AuthService _auth;
    private readonly DatabaseService _db;
    private readonly IServiceProvider _services;
    private readonly ILogger<MainPage> _logger;
    private string _simboloMoneda = "RD$";

    public MainPage(AuthService auth, DatabaseService db, IServiceProvider services, ILogger<MainPage> logger)
    {
        InitializeComponent();
        _auth = auth;
        _db = db;
        _services = services;
        _logger = logger;
        BienvenidaLabel.Text = $"¡Bienvenido, {_auth.CurrentUser?.Nombre}!";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para ver el resumen.");
            var facturas = await _db.GetFacturasAsync(uid);
            var notas = await _db.GetAllNotasCreditoAsync(uid);
            var cotizaciones = await _db.GetCotizacionesAsync(uid);
            var ajustes = await _db.ObtenerAjustesAsync(uid);
            _simboloMoneda = string.IsNullOrWhiteSpace(ajustes.SimboloMoneda) ? "RD$" : ajustes.SimboloMoneda;
            var inicioHoy = DateTime.Today.ToUniversalTime();
            var finHoy = DateTime.Today.AddDays(1).ToUniversalTime();
            var inicioMes = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToUniversalTime();
            var emitidasHoy = facturas.Where(f => f.Estado == "Emitida" && f.Fecha >= inicioHoy && f.Fecha < finHoy).ToList();
            var facturasMes = facturas.Where(f => f.NCF.Length > 0 && (f.Estado == "Emitida" || f.Estado == "Anulada") && f.Fecha >= inicioMes).ToList();
            var notasMes = notas.Where(n => !n.Anulada && n.Fecha >= inicioMes).ToList();
            var licencia = await _db.ObtenerConfiguracionUsuarioAsync(uid);
            var resumen = await _db.ObtenerResumenMensualAsync(uid);
            FacturasLabel.Text = emitidasHoy.Count.ToString();
            VentasHoyLabel.Text = Formatear(emitidasHoy.Sum(f => f.Total) - notas.Where(n => !n.Anulada && n.Fecha >= inicioHoy && n.Fecha < finHoy).Sum(n => n.Total));
            VentasMesLabel.Text = Formatear(facturasMes.Sum(f => f.Total) - notasMes.Sum(n => n.Total));
            ItbisLabel.Text = Formatear(resumen.TotalItbis);
            CotizacionesLabel.Text = cotizaciones.Count(c => c.Fecha >= inicioMes).ToString();
            LicenciaLabel.Text = LicenciaService.LicenciaActiva(licencia)
                ? $"⭐ Plan PRO activo — vence {licencia.LicenciaVencimiento?.ToLocalTime():dd/MM/yyyy}"
                : "🆓 Plan LITE (Gratis) — hasta 40 facturas al año y 5 en 30 días";
            var pendientes = await _db.GetFacturasPendientesAsync(uid);
            PendientesLabel.Text = pendientes.Count == 0
                ? "No hay facturas pendientes."
                : "Facturas pendientes / próximos vencimientos:\n" + string.Join("\n", pendientes.OrderBy(f => f.VenceFecha ?? f.Fecha).Take(5).Select(f => $"{f.NCF} — {f.ClienteNombre} — {Formatear(f.Total)}{(f.VenceFecha is DateTime vence ? $" — vence {vence.ToLocalTime():dd/MM/yyyy}" : string.Empty)}"));
            MensajeLabel.Text = "✅ Ponceano Facturador — Versión Completa\nMódulos: Autenticación · Clientes · Artículos · Cotizaciones · Facturas · Notas de Crédito · Reportes · Ajustes · Licencias\n¡Listo para usar!";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo cargar el resumen del inicio.");
            MensajeLabel.Text = "No se pudo cargar el resumen. Inténtalo de nuevo.";
        }
    }

    private string Formatear(decimal monto) => $"{_simboloMoneda} {monto:N2}";

    private async void OnNuevaFacturaClicked(object? sender, EventArgs e) => await NavegarAsync("//modulo-0");
    private async void OnNuevoClienteClicked(object? sender, EventArgs e) => await NavegarAsync("//modulo-3");
    private async void OnNuevaCotizacionClicked(object? sender, EventArgs e) => await NavegarAsync("//modulo-1");
    private async void OnExportarClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PushAsync(ActivatorUtilities.CreateInstance<ExportarDatosPage>(_services)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir Exportar Datos."); MensajeLabel.Text = "No se pudo abrir Exportar Datos."; }
    }
    private async void OnLicenciaClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PushAsync(ActivatorUtilities.CreateInstance<LicenciaPage>(_services)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir Mi Suscripción."); MensajeLabel.Text = "No se pudo abrir Mi Suscripción."; }
    }

    private async Task NavegarAsync(string ruta)
    {
        try { await Shell.Current.GoToAsync(ruta); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo navegar a {Ruta}.", ruta);
            MensajeLabel.Text = "No se pudo abrir el módulo solicitado.";
        }
    }
}
