using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class ResumenPage : ContentPage
{
    private readonly AuthService _auth;
    private readonly DatabaseService _db;
    private readonly LicenciaService _licencia;
    private readonly RelojNtpService _relojNtp;
    private readonly IServiceProvider _services;
    private readonly ILogger<ResumenPage> _logger;
    private List<ReporteVentaFila> _ventas = new();
    private List<ReporteClienteFila> _clientes = new();
    private string _simboloMoneda = "RD$";
    private string _formatoFecha = "dd/MM/yyyy";
    private readonly ResumenGraficoDrawable _grafico = new();
    private string _tipoResumen = "Factura";
    private bool _resumenInicializado;
    private int _versionResumen;

    public ResumenPage(AuthService auth, DatabaseService db, LicenciaService licencia, RelojNtpService relojNtp, IServiceProvider services, ILogger<ResumenPage> logger)
    {
        InitializeComponent();
        _auth = auth; _db = db; _licencia = licencia; _relojNtp = relojNtp; _services = services; _logger = logger;
        BienvenidaLabel.Text = $"¡Bienvenido, {_auth.CurrentUser?.Nombre}!";
        PeriodoPicker.ItemsSource = new[] { "Este mes", "Mes anterior", "Este año", "Personalizado" };
        PeriodoPicker.SelectedIndex = 0;
        OrdenClientesPicker.ItemsSource = new[] { "Más compras", "Mayor total gastado", "Nombre" };
        OrdenClientesPicker.SelectedIndex = 0;
        DesdePicker.Date = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        HastaPicker.Date = DateTime.Today;
        ResumenDesdePicker.Date = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-11);
        ResumenHastaPicker.Date = DateTime.Today;
        ResumenGrafico.Drawable = _grafico;
        _resumenInicializado = true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await CargarResumenAsync();
        await GenerarReporteAsync();
        await CargarResumenMensualAsync();
    }

    private async void OnResumenFacturaClicked(object? sender, EventArgs e) => await CambiarTipoResumenAsync("Factura");
    private async void OnResumenCotizacionClicked(object? sender, EventArgs e) => await CambiarTipoResumenAsync("Cotización");
    private async void OnResumenNotaClicked(object? sender, EventArgs e) => await CambiarTipoResumenAsync("Nota de crédito");
    private async void OnResumenPagadoClicked(object? sender, EventArgs e) => await CambiarTipoResumenAsync("Pagado");
    private async void OnResumenFechaChanged(object? sender, DateChangedEventArgs e)
    {
        if (_resumenInicializado) await CargarResumenMensualAsync();
    }

    private async Task CambiarTipoResumenAsync(string tipo)
    {
        _tipoResumen = tipo;
        await CargarResumenMensualAsync();
    }

    private async Task CargarResumenMensualAsync()
    {
        var version = ++_versionResumen;
        try
        {
            var desde = (ResumenDesdePicker.Date ?? DateTime.Today).Date;
            var hasta = (ResumenHastaPicker.Date ?? DateTime.Today).Date;
            if (desde > hasta) { LimpiarResumenMensual("La fecha inicial no puede ser posterior a la fecha final."); return; }
            var primerMes = new DateTime(desde.Year, desde.Month, 1);
            var ultimoMes = new DateTime(hasta.Year, hasta.Month, 1);
            var numeroMeses = (ultimoMes.Year - primerMes.Year) * 12 + ultimoMes.Month - primerMes.Month + 1;
            if (numeroMeses > 120) { LimpiarResumenMensual("Selecciona un período de hasta 120 meses para el resumen."); return; }
            var tipo = _tipoResumen;
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para ver el resumen.");
            var inicioUtc = desde.ToUniversalTime();
            var finUtc = hasta.AddDays(1).ToUniversalTime();
            List<(DateTime Fecha, string Documento, string Cliente, decimal Total)> documentos;
            if (tipo == "Cotización")
                documentos = (await _db.GetCotizacionesAsync(uid)).Where(c => c.Fecha >= inicioUtc && c.Fecha < finUtc)
                    .Select(c => (c.Fecha, c.Id.ToString("N")[..8], c.ClienteNombre, c.Total)).ToList();
            else if (tipo == "Nota de crédito")
                documentos = (await _db.GetAllNotasCreditoAsync(uid)).Where(n => !n.Anulada && !n.EsBorrador && n.Fecha >= inicioUtc && n.Fecha < finUtc)
                    .Select(n => (n.Fecha, n.NCF, n.ClienteNombre, n.Total)).ToList();
            else
                documentos = (await _db.GetFacturasAsync(uid)).Where(f => f.Estado == "Emitida" && !f.Anulada && f.Fecha >= inicioUtc && f.Fecha < finUtc && (tipo != "Pagado" || f.EstadoPago == "Pagada"))
                    .Select(f => (f.Fecha, f.NCF, f.ClienteNombre, f.Total)).ToList();
            if (version != _versionResumen) return;

            foreach (var (boton, nombre) in new[] { (ResumenFacturaButton, "Factura"), (ResumenCotizacionButton, "Cotización"), (ResumenNotaButton, "Nota de crédito"), (ResumenPagadoButton, "Pagado") })
            {
                boton.BackgroundColor = nombre == tipo ? Color.FromArgb("#55B85B") : Color.FromArgb("#F5F5F5");
                boton.TextColor = nombre == tipo ? Colors.White : Color.FromArgb("#555555");
            }
            var meses = Enumerable.Range(0, numeroMeses).Select(i => primerMes.AddMonths(i))
                .Select(mes => (Mes: mes, Total: documentos.Where(d => d.Fecha.ToLocalTime().Year == mes.Year && d.Fecha.ToLocalTime().Month == mes.Month).Sum(d => d.Total))).ToList();
            _grafico.Datos = meses;
            ResumenGrafico.Invalidate();
            ResumenEstadoLabel.Text = string.Empty;
            ResumenPagadoInfoLabel.IsVisible = tipo == "Pagado";
            ResumenTotalLabel.Text = $"Total: {FormatearMoneda(documentos.Sum(d => d.Total))}";
            ResumenMesesLayout.Children.Clear();
            foreach (var mes in meses.AsEnumerable().Reverse())
            {
                var documentosMes = documentos.Where(d => d.Fecha.ToLocalTime().Year == mes.Mes.Year && d.Fecha.ToLocalTime().Month == mes.Mes.Month).OrderByDescending(d => d.Fecha).ToList();
                var fila = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Star), new(GridLength.Auto) }, Padding = new Thickness(8, 3), BackgroundColor = Color.FromArgb("#FAFAFA") };
                fila.Add(new Label { Text = mes.Mes.ToString("MM/yyyy"), TextColor = Color.FromArgb("#777777"), HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.Center }, 0);
                fila.Add(new Label { Text = FormatearMoneda(mes.Total), TextColor = Color.FromArgb("#777777"), HorizontalTextAlignment = TextAlignment.End, VerticalOptions = LayoutOptions.Center }, 1);
                var detalle = new Button { Text = "Más", IsEnabled = documentosMes.Count > 0, BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#268DD0"), Padding = new Thickness(8, 0), MinimumHeightRequest = 32 };
                detalle.Clicked += async (_, _) => await DisplayAlertAsync($"{tipo} — {mes.Mes:MM/yyyy}", string.Join("\n", documentosMes.Select(d => $"{d.Fecha.ToLocalTime():dd/MM/yyyy}  {d.Documento}  {d.Cliente}  {FormatearMoneda(d.Total)}")), "Cerrar");
                fila.Add(detalle, 2);
                ResumenMesesLayout.Children.Add(fila);
            }
            MensajeLabel.Text = string.Empty;
        }
        catch (Exception ex)
        {
            if (version != _versionResumen) return;
            _logger.LogError(ex, "No se pudo cargar el resumen mensual.");
            LimpiarResumenMensual("No se pudo cargar el resumen mensual. Inténtalo de nuevo.");
        }
    }

    private void LimpiarResumenMensual(string mensaje)
    {
        _grafico.Datos = Array.Empty<(DateTime, decimal)>();
        ResumenGrafico.Invalidate();
        ResumenMesesLayout.Children.Clear();
        ResumenTotalLabel.Text = string.Empty;
        ResumenPagadoInfoLabel.IsVisible = false;
        ResumenEstadoLabel.Text = mensaje;
    }

    private async Task CargarResumenAsync()
    {
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para ver el resumen.");
            var resumen = await _db.ObtenerResumenMensualAsync(uid);
            var estado = await _licencia.ObtenerEstadoLicenciaAsync();
            var ajustes = await _db.ObtenerAjustesAsync(uid);
            _simboloMoneda = string.IsNullOrWhiteSpace(ajustes.SimboloMoneda) ? "RD$" : ajustes.SimboloMoneda;
            _formatoFecha = ajustes.FormatoFecha;
            var esPrueba = estado.FechaInicioPruebaUtc.HasValue && estado.FechaVencimientoPruebaUtc.HasValue;
            var activa = LicenciaService.LicenciaActiva(estado);
            var hoyInicio = DateTime.Today.ToUniversalTime();
            var manana = DateTime.Today.AddDays(1).ToUniversalTime();
            var facturas = await _db.GetFacturasAsync(uid);
            var notas = await _db.GetAllNotasCreditoAsync(uid);
            var emitidasHoy = facturas.Where(f => f.Estado == "Emitida" && !f.Anulada && f.Fecha >= hoyInicio && f.Fecha < manana).ToList();
            var notasHoy = notas.Where(n => !n.Anulada && !n.EsBorrador && n.Fecha >= hoyInicio && n.Fecha < manana).Sum(n => n.Total);
            FacturasHoyLabel.Text = emitidasHoy.Count.ToString(CultureInfo.CurrentCulture);
            TotalDiaLabel.Text = FormatearMoneda(emitidasHoy.Sum(f => f.Total) - notasHoy);
            VentasLabel.Text = FormatearMoneda(resumen.TotalVentas);
            ItbisLabel.Text = FormatearMoneda(resumen.TotalItbis);
            LicenciaLabel.Text = esPrueba ? "🔑 PRUEBA GRATUITA DE 30 DÍAS"
                : estado.LicenciaTipo == "Mensual" ? LicenciaPruebaService.MensajeLicenciaCompletaActiva
                : $"🔑 TU LICENCIA: {estado.LicenciaTipo}";
            VenceLabel.Text = estado.LicenciaVencimiento is DateTime vence ? $"Vence: {vence.ToLocalTime().ToString(_formatoFecha, CultureInfo.CurrentCulture)}" : "Vence: No aplica";
            DiasPruebaLabel.IsVisible = esPrueba;
            if (esPrueba)
            {
                try
                {
                    var horaNtp = await _relojNtp.ObtenerHoraUtcAsync();
                    var diasRestantes = Math.Max(0, (int)Math.Ceiling((LicenciaPruebaService.AsegurarUtc(estado.FechaVencimientoPruebaUtc!.Value) - horaNtp.UtcDateTime).TotalDays));
                    DiasPruebaLabel.Text = $"DÍAS RESTANTES DE PRUEBA: {diasRestantes} DÍAS";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo actualizar el contador de la prueba con hora NTP.");
                    DiasPruebaLabel.Text = "DÍAS RESTANTES DE PRUEBA: requiere conexión a internet para validar la hora";
                }
            }
            if (activa && estado.LicenciaVencimiento is DateTime vencimiento)
            {
                var dias = (vencimiento.ToLocalTime().Date - DateTime.Today).Days;
                if (dias is >= 0 and <= 7) MensajeLabel.Text = $"Tu licencia vence en {dias} días.";
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo cargar el panel de resumen."); MensajeLabel.Text = "No se pudo cargar el resumen. Inténtalo de nuevo."; }
    }

    private void OnPeriodoChanged(object? sender, EventArgs e)
    {
        var personalizado = PeriodoPicker.SelectedItem?.ToString() == "Personalizado";
        DesdePicker.IsEnabled = personalizado;
        HastaPicker.IsEnabled = personalizado;
        if (personalizado) return;
        var hoy = DateTime.Today;
        switch (PeriodoPicker.SelectedItem?.ToString())
        {
            case "Mes anterior":
                DesdePicker.Date = new DateTime(hoy.Year, hoy.Month, 1).AddMonths(-1);
                HastaPicker.Date = new DateTime(hoy.Year, hoy.Month, 1).AddDays(-1);
                break;
            case "Este año":
                DesdePicker.Date = new DateTime(hoy.Year, 1, 1);
                HastaPicker.Date = hoy;
                break;
            default:
                DesdePicker.Date = new DateTime(hoy.Year, hoy.Month, 1);
                HastaPicker.Date = hoy;
                break;
        }
    }

    private async void OnGenerarVentasClicked(object? sender, EventArgs e) => await GenerarReporteAsync();

    private async Task GenerarReporteAsync()
    {
        try
        {
            var desde = (DesdePicker.Date ?? DateTime.Today).Date;
            var hasta = (HastaPicker.Date ?? DateTime.Today).Date;
            if (desde > hasta) { MensajeLabel.Text = "La fecha inicial no puede ser posterior a la fecha final."; return; }
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para generar reportes.");
            var desdeUtc = desde.ToUniversalTime();
            var hastaUtc = hasta.AddDays(1).ToUniversalTime();
            var facturas = (await _db.GetFacturasAsync(uid)).Where(f => f.NCF.Length > 0 && (f.Estado == "Emitida" || f.Estado == "Anulada") && f.Fecha >= desdeUtc && f.Fecha < hastaUtc).ToList();
            var notas = (await _db.GetAllNotasCreditoAsync(uid)).Where(n => !n.Anulada && !n.EsBorrador && n.Fecha >= desdeUtc && n.Fecha < hastaUtc).ToList();
            _ventas = facturas.Select(f => new ReporteVentaFila(f.Fecha, f.NCF, f.ClienteId, f.ClienteNombre, f.Subtotal + f.RecargoMonto, f.Itbis, f.Total, _simboloMoneda, _formatoFecha, false))
                .Concat(notas.Select(n => new ReporteVentaFila(n.Fecha, n.NCF + " (NC)", n.ClienteId, n.ClienteNombre, -n.Subtotal, -n.Itbis, -n.Total, _simboloMoneda, _formatoFecha, true)))
                .OrderBy(f => f.Fecha).ToList();
            VentasLista.ItemsSource = _ventas;
            VentasVacioLabel.IsVisible = _ventas.Count == 0;
            TotalesVentasLabel.Text = $"TOTALES: Subtotal {FormatearMoneda(_ventas.Sum(v => v.Subtotal))}  ITBIS {FormatearMoneda(_ventas.Sum(v => v.Itbis))}  Total {FormatearMoneda(_ventas.Sum(v => v.Total))}";
            var porCliente = _ventas.GroupBy(v => v.ClienteId)
                .Select(g => new ReporteClienteFila(g.First().Cliente, g.Count(v => !v.EsNota), g.Sum(v => v.Total), _simboloMoneda))
                .Where(c => c.Compras > 0).ToList();
            _clientes = porCliente;
            OrdenarClientes();
            ClientesVacioLabel.IsVisible = _clientes.Count == 0;
            MensajeLabel.Text = string.Empty;
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo generar el reporte."); MensajeLabel.Text = "No se pudo generar el reporte. Inténtalo de nuevo."; }
    }

    private void OnOrdenClientesChanged(object? sender, EventArgs e) => OrdenarClientes();

    private void OrdenarClientes()
    {
        var orden = OrdenClientesPicker.SelectedItem?.ToString();
        ClientesLista.ItemsSource = orden switch
        {
            "Mayor total gastado" => _clientes.OrderByDescending(c => c.Total).ThenBy(c => c.Nombre).ToList(),
            "Nombre" => _clientes.OrderBy(c => c.Nombre).ToList(),
            _ => _clientes.OrderByDescending(c => c.Compras).ThenBy(c => c.Nombre).ToList()
        };
    }

    private void OnVentasTabClicked(object? sender, EventArgs e) { VentasReporte.IsVisible = true; ClientesReporte.IsVisible = false; }
    private void OnClientesTabClicked(object? sender, EventArgs e) { VentasReporte.IsVisible = false; ClientesReporte.IsVisible = true; }

    private async void OnLicenciaClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PushAsync(Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<LicenciaPage>(_services)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir la información del plan."); MensajeLabel.Text = "No se pudo abrir la información del plan."; }
    }

    private async void OnNuevaFacturaClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!await _licencia.PuedeCrearFacturaAsync()) { MensajeLabel.Text = "Límite LITE alcanzado. Consulta la información del plan."; return; }
            await Navigation.PushAsync(Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<FacturaEditarPage>(_services));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir el formulario de factura."); MensajeLabel.Text = ex.Message; }
    }

    private async void OnNuevoClienteClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PushAsync(Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<ClienteEditarPage>(_services)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir el formulario de cliente."); MensajeLabel.Text = "No se pudo abrir el formulario de cliente."; }
    }

    private async void OnNuevaCotizacionClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!await _licencia.PuedeCrearCotizacionAsync()) { MensajeLabel.Text = "Límite gratuito de cotizaciones alcanzado este mes."; return; }
            await Navigation.PushAsync(Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<CotizacionEditarPage>(_services));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir el formulario de cotización."); MensajeLabel.Text = ex.Message; }
    }

    private async void OnExportarDatosClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PushAsync(Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<ExportarDatosPage>(_services)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir Exportar Datos."); MensajeLabel.Text = "No se pudo abrir Exportar Datos."; }
    }

    private async void OnExportarCsvClicked(object? sender, EventArgs e)
    {
        try
        {
            var lineas = new List<string> { "Fecha,Documento,Cliente,Subtotal,ITBIS,Total" };
            lineas.AddRange(_ventas.Select(v => string.Join(',', Csv(v.Fecha.ToString(_formatoFecha, CultureInfo.CurrentCulture)), Csv(v.Documento), Csv(v.Cliente), v.Subtotal.ToString("F2", CultureInfo.InvariantCulture), v.Itbis.ToString("F2", CultureInfo.InvariantCulture), v.Total.ToString("F2", CultureInfo.InvariantCulture))));
            var path = Path.Combine(FileSystem.CacheDirectory, "reporte-ventas.csv");
            await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lineas), new UTF8Encoding(true));
            await Share.Default.RequestAsync(new ShareFileRequest("Reporte de ventas CSV", new ShareFile(path)));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo exportar CSV."); MensajeLabel.Text = "No se pudo exportar el archivo CSV."; }
    }

    private async void OnExportarPdfClicked(object? sender, EventArgs e)
    {
        try
        {
            var desde = DesdePicker.Date ?? DateTime.Today;
            var hasta = HastaPicker.Date ?? DateTime.Today;
            var lineas = new List<string> { "Reporte de ventas", $"Periodo: {desde.ToString(_formatoFecha)} - {hasta.ToString(_formatoFecha)}", "Fecha | Documento | Cliente | Subtotal | ITBIS | Total" };
            lineas.AddRange(_ventas.Select(v => $"{v.Fecha.ToString(_formatoFecha)} | {v.Documento} | {v.Cliente} | {FormatearMoneda(v.Subtotal)} | {FormatearMoneda(v.Itbis)} | {FormatearMoneda(v.Total)}"));
            lineas.Add(TotalesVentasLabel.Text ?? string.Empty);
            var path = Path.Combine(FileSystem.CacheDirectory, "reporte-ventas.pdf");
            await File.WriteAllBytesAsync(path, CrearPdf(lineas));
            await Share.Default.RequestAsync(new ShareFileRequest("Reporte de ventas PDF", new ShareFile(path)));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo exportar PDF."); MensajeLabel.Text = "No se pudo generar el reporte PDF."; }
    }

    private string FormatearMoneda(decimal valor) => $"{_simboloMoneda} {valor:N2}";
    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        var content = text.TrimStart();
        if (content.Length > 0 && content[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n') text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    internal static byte[] CrearPdf(IEnumerable<string> lineas)
    {
        var paginas = lineas.Select(LimpiarAscii).Chunk(55).ToArray();
        if (paginas.Length == 0) paginas = new[] { Array.Empty<string>() };
        var objetos = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Array.Empty<byte>(),
            Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
        };
        var referenciasPaginas = new List<string>();
        foreach (var pagina in paginas)
        {
            var numeroPagina = objetos.Count + 1;
            var numeroContenido = numeroPagina + 1;
            referenciasPaginas.Add($"{numeroPagina} 0 R");
            var contenido = new StringBuilder("BT\n/F1 9 Tf\n50 790 Td\n12 TL\n");
            foreach (var linea in pagina) contenido.Append('(').Append(linea.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj\nT*\n");
            contenido.Append("ET");
            var stream = Encoding.ASCII.GetBytes(contenido.ToString());
            objetos.Add(Encoding.ASCII.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {numeroContenido} 0 R >>"));
            objetos.Add(Encoding.ASCII.GetBytes($"<< /Length {stream.Length} >>\nstream\n{Encoding.ASCII.GetString(stream)}\nendstream"));
        }
        objetos[1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(" ", referenciasPaginas)}] /Count {paginas.Length} >>");
        using var pdf = new MemoryStream();
        void Escribir(string value) { var bytes = Encoding.ASCII.GetBytes(value); pdf.Write(bytes); }
        Escribir("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objetos.Count; i++) { offsets.Add(pdf.Position); Escribir($"{i + 1} 0 obj\n"); pdf.Write(objetos[i]); Escribir("\nendobj\n"); }
        var xref = pdf.Position;
        Escribir($"xref\n0 {objetos.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Escribir($"{offset:D10} 00000 n \n");
        Escribir($"trailer\n<< /Size {objetos.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return pdf.ToArray();
    }

    private static string LimpiarAscii(string texto)
    {
        var normalizado = texto.Normalize(NormalizationForm.FormD);
        return new string(normalizado.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && c <= 127).ToArray());
    }

    private sealed record ReporteVentaFila(DateTime Fecha, string Documento, Guid ClienteId, string Cliente, decimal Subtotal, decimal Itbis, decimal Total, string Simbolo, string FormatoFecha, bool EsNota)
    {
        public string FechaTexto => Fecha.ToLocalTime().ToString(FormatoFecha, CultureInfo.CurrentCulture);
        public string SubtotalTexto => $"Subt. {Simbolo} {Subtotal:N2}";
        public string ItbisTexto => $"ITBIS {Simbolo} {Itbis:N2}";
        public string TotalTexto => $"Total {Simbolo} {Total:N2}";
    }
    private sealed record ReporteClienteFila(string Nombre, int Compras, decimal Total, string Simbolo)
    {
        public string TotalTexto => $"{Simbolo} {Total:N2}";
    }

    private sealed record ResumenNcfFila(string Tipo, string Prefijo, int Siguiente, bool Activo)
    {
        public string SiguienteTexto => $"Siguiente: {Siguiente:D8}";
        public string Estado => Activo ? "Activo" : "Inactivo";
    }
}
