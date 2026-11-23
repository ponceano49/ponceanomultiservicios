using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class ExportarDatosPage : ContentPage
{
    private static readonly string[] Opciones = [
        "Todas las facturas del período",
        "Solo facturas pagadas",
        "Solo facturas pendientes",
        "Cotizaciones",
        "Listado de clientes"
    ];

    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly ILogger<ExportarDatosPage> _logger;

    public ExportarDatosPage(DatabaseService db, AuthService auth, ILogger<ExportarDatosPage> logger)
    {
        InitializeComponent();
        _db = db;
        _auth = auth;
        _logger = logger;
        TipoPicker.ItemsSource = Opciones;
        TipoPicker.SelectedIndex = 0;
        DesdePicker.Date = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        HastaPicker.Date = DateTime.Today;
    }

    private async void OnExportarClicked(object? sender, EventArgs e)
    {
        try
        {
            var tipo = TipoPicker.SelectedItem?.ToString() ?? throw new InvalidOperationException("Selecciona qué datos deseas exportar.");
            var desde = (DesdePicker.Date ?? DateTime.Today).Date;
            var hasta = (HastaPicker.Date ?? DateTime.Today).Date;
            if (desde > hasta) throw new InvalidOperationException("La fecha inicial no puede ser posterior a la fecha final.");
            var usuarioId = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para exportar datos.");
            var inicioUtc = desde.ToUniversalTime();
            var finUtc = hasta.AddDays(1).ToUniversalTime();
            var filas = tipo switch
            {
                "Todas las facturas del período" => FacturasCsv((await _db.GetFacturasAsync(usuarioId)).Where(f => EnPeriodo(f.Fecha, inicioUtc, finUtc) && f.NCF.Length > 0 && f.Estado == "Emitida" && !f.Anulada)),
                "Solo facturas pagadas" => FacturasCsv((await _db.GetFacturasAsync(usuarioId)).Where(f => EnPeriodo(f.Fecha, inicioUtc, finUtc) && f.NCF.Length > 0 && f.Estado == "Emitida" && !f.Anulada && f.EstadoPago == "Pagada")),
                "Solo facturas pendientes" => FacturasCsv((await _db.GetFacturasAsync(usuarioId)).Where(f => EnPeriodo(f.Fecha, inicioUtc, finUtc) && f.NCF.Length > 0 && f.Estado == "Emitida" && !f.Anulada && f.EstadoPago == "Pendiente")),
                "Cotizaciones" => CotizacionesCsv((await _db.GetCotizacionesAsync(usuarioId)).Where(c => EnPeriodo(c.Fecha, inicioUtc, finUtc))),
                "Listado de clientes" => ClientesCsv(await _db.GetAllClientesAsync()),
                _ => throw new InvalidOperationException("La opción de exportación no es válida.")
            };
            var nombre = tipo == "Listado de clientes" ? "clientes" : tipo == "Cotizaciones" ? "cotizaciones" : "facturas";
            var path = Path.Combine(FileSystem.CacheDirectory, $"{nombre}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, filas), new UTF8Encoding(true));
            await Share.Default.RequestAsync(new ShareFileRequest("Exportar datos CSV", new ShareFile(path)));
            MensajeLabel.Text = "Archivo CSV preparado para compartir o guardar.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron exportar los datos a CSV.");
            MensajeLabel.Text = ex is InvalidOperationException ? ex.Message : "No se pudieron exportar los datos. Inténtalo de nuevo.";
        }
    }

    private static bool EnPeriodo(DateTime fecha, DateTime inicioUtc, DateTime finUtc) => fecha >= inicioUtc && fecha < finUtc;

    private static List<string> FacturasCsv(IEnumerable<Factura> facturas)
    {
        var filas = new List<string> { "Fecha,Documento,Cliente,Estado,Estado de pago,Método de pago,Subtotal,ITBIS,Recargo,Total,Vencimiento" };
        filas.AddRange(facturas.OrderBy(f => f.Fecha).Select(f => string.Join(',',
            Campo(f.Fecha.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)), Campo(f.NCF), Campo(f.ClienteNombre),
            Campo(f.Estado), Campo(f.EstadoPago), Campo(f.MetodoPago), Monto(f.Subtotal), Monto(f.Itbis), Monto(f.RecargoMonto), Monto(f.Total),
            Campo(f.VenceFecha?.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)))));
        return filas;
    }

    private static List<string> CotizacionesCsv(IEnumerable<Cotizacion> cotizaciones)
    {
        var filas = new List<string> { "Fecha,Documento,Cliente,Estado,Subtotal,ITBIS,Total" };
        filas.AddRange(cotizaciones.OrderBy(c => c.Fecha).Select(c => string.Join(',',
            Campo(c.Fecha.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)), Campo(c.Id.ToString()), Campo(c.ClienteNombre),
            Campo(c.ConvertidaAFactura ? "Convertida a factura" : "Pendiente"), Monto(c.Subtotal), Monto(c.Itbis), Monto(c.Total))));
        return filas;
    }

    private static List<string> ClientesCsv(IEnumerable<Cliente> clientes)
    {
        var filas = new List<string> { "Nombre,RNC/Cédula,Teléfono,Correo,Dirección" };
        filas.AddRange(clientes.OrderBy(c => c.Nombre).Select(c => string.Join(',', Campo(c.Nombre), Campo(c.RncCedula), Campo(c.Telefono), Campo(c.Correo), Campo(c.Direccion))));
        return filas;
    }

    private static string Monto(decimal valor) => valor.ToString("F2", CultureInfo.InvariantCulture);

    private static string Campo(string? valor)
    {
        var texto = valor ?? string.Empty;
        var contenido = texto.TrimStart();
        if (contenido.Length > 0 && contenido[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n') texto = "'" + texto;
        return "\"" + texto.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private async void OnCancelarClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//modulo-0"); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo volver al inicio desde exportar datos."); MensajeLabel.Text = "No se pudo volver al inicio."; }
    }
}
