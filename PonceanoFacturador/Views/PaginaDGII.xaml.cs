using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class PaginaDGII : ContentPage
{
    private readonly ILogger<PaginaDGII> _logger;
    private readonly AuthService _auth;
    private readonly DatabaseService _db;
    private readonly LecturaDocumentoFiscalService _lector;
    private readonly ValidacionFiscalOllamaService _validacionFiscal;
    private readonly BorradorFiscal606607OllamaService _borradorFiscal;
    private readonly DirectorioProveedoresService _proveedores;
    private readonly ImpresionWindowsService _impresion;
    private readonly TesseractOcrService _tesseract;
    private readonly string _raiz;
    private readonly string _carpeta607;
    private readonly string _carpeta606;
    private readonly string _carpetaEntrada;
    private readonly string _carpetaProcesadas;
    private readonly string _carpetaErrores;
    private readonly string _carpetaBorradores;
    private readonly Dictionary<string, (long Longitud, DateTime Modificado)> _archivosObservados = new(StringComparer.OrdinalIgnoreCase);
    private Microsoft.Maui.Dispatching.IDispatcherTimer? _temporizadorMonitoreo;
    private CancellationTokenSource? _cancelacionMonitoreo;
    private bool _procesando;
    private bool _actualizandoTasa;
    private int _modoDiseno = -1;
    private string _tasaManual = "59.85";
    private List<TurnoCaja> _turnosFiltro = new();

    public PaginaDGII(ILogger<PaginaDGII> logger, AuthService auth, DatabaseService db, LecturaDocumentoFiscalService lector, ValidacionFiscalOllamaService validacionFiscal, BorradorFiscal606607OllamaService borradorFiscal, DirectorioProveedoresService proveedores, ImpresionWindowsService impresion, TesseractOcrService tesseract)
    {
        InitializeComponent();
        _logger = logger;
        _auth = auth;
        _db = db;
        _lector = lector;
        _validacionFiscal = validacionFiscal;
        _borradorFiscal = borradorFiscal;
        _proveedores = proveedores;
        _impresion = impresion;
        _tesseract = tesseract;
        _raiz = RutasAplicacion.DirectorioDgii;
        _carpeta607 = Path.Combine(_raiz, "607 Ventas");
        _carpeta606 = Path.Combine(_raiz, "606 Compras");
        _carpetaEntrada = Path.Combine(_raiz, "Cargas OCR", "Para procesar");
        _carpetaProcesadas = Path.Combine(_raiz, "Cargas OCR", "Procesadas");
        _carpetaErrores = Path.Combine(_raiz, "Cargas OCR", "Errores");
        _carpetaBorradores = Path.Combine(_raiz, "Borradores 606-607");
        RutaLabel.Text = $"Reportes y borradores: {_raiz}";
        PeriodoDesdePicker.Date = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        PeriodoHastaPicker.Date = DateTime.Today;
        ActualizarEtiquetaTasaManual();
    }

    private void OnTasaCambioChanged(object? sender, TextChangedEventArgs e)
    {
        if (_actualizandoTasa) return;
        _tasaManual = e.NewTextValue ?? string.Empty;
        if (TasaManualLabel is not null) ActualizarEtiquetaTasaManual();
    }

    private void ActualizarEtiquetaTasaManual() => TasaManualLabel.Text = $"Tasa manual para facturas USD sin tasa: {_tasaManual}. Edita el campo para cambiarla; la tasa detectada se muestra solo para la factura actual.";

    private void MostrarTasa(AnalisisMonedaOcr analisis)
    {
        _actualizandoTasa = true;
        try { TasaCambioEntry.Text = analisis.Tasa.ToString("0.00####", CultureInfo.InvariantCulture); }
        finally { _actualizandoTasa = false; }
    }

    private AnalisisMonedaOcr AnalizarMoneda(string texto) =>
        FacturaOcrParser.AnalizarMoneda(texto, TryLeerTasaCambio(_tasaManual, out var manual) ? manual : null);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        AsegurarCarpetas();
        await CargarTurnosFiltroAsync();
    }

    private async Task CargarTurnosFiltroAsync()
    {
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase;
            _turnosFiltro = string.IsNullOrWhiteSpace(uid) ? new List<TurnoCaja>() : await _db.ObtenerTurnosAsync(uid);
            TurnoFiltroPicker.ItemsSource = new[] { "Todos los turnos" }.Concat(_turnosFiltro.Select(turno => $"Turno {turno.NumeroTurno} · {turno.Fecha:dd/MM/yyyy} · {turno.Estado}" )).ToList();
            TurnoFiltroPicker.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo cargar el filtro de turnos para los reportes fiscales.");
            EstadoLabel.Text = "No se pudo cargar el filtro de turnos.";
        }
    }

    protected override void OnDisappearing()
    {
        DetenerMonitoreo();
        base.OnDisappearing();
    }

    private async void OnLeerTesseractClicked(object? sender, EventArgs e)
    {
        LeerTesseractButton.IsEnabled = false;
        try
        {
            var archivo = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Selecciona una factura PDF o imagen",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.WinUI] = new[] { ".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" },
                    [DevicePlatform.Android] = new[] { "application/pdf", "image/*" },
                    [DevicePlatform.iOS] = new[] { "com.adobe.pdf", "public.image" },
                    [DevicePlatform.MacCatalyst] = new[] { "com.adobe.pdf", "public.image" }
                })
            });
            if (archivo is null) return;

            TextoTesseractEditor.Text = string.Empty;
            EstadoTesseractLabel.Text = $"📂 Archivo cargado: {archivo.FileName}\n🔍 Leyendo documento...";
            var texto = await _tesseract.LeerTextoDeImagenAsync(archivo.FullPath);
            TextoTesseractEditor.Text = texto;
            string resumenMoneda;
            try
            {
                var analisis = AnalizarMoneda(texto);
                MostrarTasa(analisis);
                resumenMoneda = FacturaOcrParser.ResumenMoneda(analisis);
            }
            catch (InvalidDataException ex)
            {
                resumenMoneda = $"⚠️ Moneda o tasa requiere revisión: {ex.Message}";
            }
            var ruta = await _tesseract.GuardarLecturaAsync(archivo.FileName, texto, resumenMoneda: resumenMoneda);
            EstadoTesseractLabel.Text = $"✅ Lectura completada. Texto guardado en la carpeta Reportes\\OCR\\Leidos\\\nRuta: {ruta}\n{resumenMoneda}";
            await DisplayAlertAsync("Lectura OCR", EstadoTesseractLabel.Text, "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo leer o guardar la factura con Tesseract.");
            EstadoTesseractLabel.Text = $"❌ Error de lectura Tesseract: {ex.Message}";
            await DisplayAlertAsync("Lectura OCR", EstadoTesseractLabel.Text, "Aceptar");
        }
        finally
        {
            LeerTesseractButton.IsEnabled = true;
        }
    }

    private void OnPanelesDGIISizeChanged(object? sender, EventArgs e)
    {
        var modo = PanelesDGII.Width >= 1100 ? 0 : PanelesDGII.Width >= 720 ? 1 : 2;
        if (modo == _modoDiseno) return;
        _modoDiseno = modo;
        if (modo == 0)
        {
            PanelesDGII.ColumnDefinitions.Clear();
            PanelesDGII.ColumnDefinitions.Add(new ColumnDefinition(240));
            PanelesDGII.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            PanelesDGII.ColumnDefinitions.Add(new ColumnDefinition(240));
            PanelesDGII.RowDefinitions.Clear();
            PanelesDGII.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(CentroPanel, 0);
            Grid.SetColumn(CentroPanel, 1);
            Grid.SetColumnSpan(CentroPanel, 1);
            Grid.SetRow(PanelIzquierdo, 0);
            Grid.SetColumn(PanelIzquierdo, 0);
            Grid.SetColumnSpan(PanelIzquierdo, 1);
            Grid.SetRow(PanelDerecho, 0);
            Grid.SetColumn(PanelDerecho, 2);
            Grid.SetColumnSpan(PanelDerecho, 1);
        }
        else if (modo == 1)
        {
            PanelesDGII.ColumnDefinitions.Clear();
            PanelesDGII.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            PanelesDGII.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            PanelesDGII.RowDefinitions.Clear();
            PanelesDGII.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            PanelesDGII.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(CentroPanel, 0);
            Grid.SetColumn(CentroPanel, 0);
            Grid.SetColumnSpan(CentroPanel, 2);
            Grid.SetRow(PanelIzquierdo, 1);
            Grid.SetColumn(PanelIzquierdo, 0);
            Grid.SetColumnSpan(PanelIzquierdo, 1);
            Grid.SetRow(PanelDerecho, 1);
            Grid.SetColumn(PanelDerecho, 1);
            Grid.SetColumnSpan(PanelDerecho, 1);
        }
        else
        {
            PanelesDGII.ColumnDefinitions.Clear();
            PanelesDGII.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            PanelesDGII.RowDefinitions.Clear();
            PanelesDGII.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            PanelesDGII.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            PanelesDGII.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(CentroPanel, 0);
            Grid.SetColumn(CentroPanel, 0);
            Grid.SetColumnSpan(CentroPanel, 1);
            Grid.SetRow(PanelIzquierdo, 1);
            Grid.SetColumn(PanelIzquierdo, 0);
            Grid.SetColumnSpan(PanelIzquierdo, 1);
            Grid.SetRow(PanelDerecho, 2);
            Grid.SetColumn(PanelDerecho, 0);
            Grid.SetColumnSpan(PanelDerecho, 1);
        }
    }

    private void OnMonitoreoClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        if (_temporizadorMonitoreo is not null)
        {
            DetenerMonitoreo();
            EstadoLabel.Text = "Monitoreo automático detenido.";
            return;
        }
        if (!AsegurarCarpetas()) return;
        if (string.IsNullOrWhiteSpace(_auth.CurrentUser?.IdFirebase) || !_validacionFiscal.EstaConfigurado)
        {
            EstadoLabel.Text = "Inicia sesión y configura DGII_OLLAMA_MODEL antes de activar el monitoreo.";
            return;
        }
        _cancelacionMonitoreo = new CancellationTokenSource();
        _temporizadorMonitoreo = Dispatcher.CreateTimer();
        _temporizadorMonitoreo.Interval = TimeSpan.FromSeconds(5);
        _temporizadorMonitoreo.Tick += OnMonitoreoTick;
        _temporizadorMonitoreo.Start();
        MonitoreoButton.Text = "⏹️ Detener monitoreo automático";
        EstadoLabel.Text = "Monitoreo activo: los archivos compatibles se procesarán localmente.";
#else
        EstadoLabel.Text = "El monitoreo de la carpeta está disponible únicamente en Windows.";
#endif
    }

    private void DetenerMonitoreo()
    {
        if (_temporizadorMonitoreo is not null)
        {
            _temporizadorMonitoreo.Stop();
            _temporizadorMonitoreo.Tick -= OnMonitoreoTick;
            _temporizadorMonitoreo = null;
        }
        _cancelacionMonitoreo?.Cancel();
        _cancelacionMonitoreo?.Dispose();
        _cancelacionMonitoreo = null;
        _archivosObservados.Clear();
        MonitoreoButton.Text = "🔄 Activar monitoreo automático";
    }

    private async void OnMonitoreoTick(object? sender, EventArgs e)
    {
        if (_procesando || _cancelacionMonitoreo is null) return;
        try
        {
            var disponibles = new List<string>();
            var presentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ruta in Directory.EnumerateFiles(_carpetaEntrada).Where(EsArchivoOcr))
            {
                presentes.Add(ruta);
                var info = new FileInfo(ruta);
                var estado = (info.Length, info.LastWriteTimeUtc);
                if (_archivosObservados.TryGetValue(ruta, out var anterior) && anterior == estado &&
                    DateTime.UtcNow - info.LastWriteTimeUtc >= TimeSpan.FromSeconds(10))
                {
                    try
                    {
                        using var comprobacion = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.None);
                        disponibles.Add(ruta);
                    }
                    catch (IOException) { continue; }
                }
                _archivosObservados[ruta] = estado;
            }
            foreach (var ruta in _archivosObservados.Keys.Where(ruta => !presentes.Contains(ruta)).ToList())
                _archivosObservados.Remove(ruta);
            if (disponibles.Count > 0)
                await ProcesarCarpetaAsync(disponibles, _cancelacionMonitoreo.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo monitorear la carpeta OCR.");
            DetenerMonitoreo();
            EstadoLabel.Text = "No se pudo monitorear la carpeta OCR. Verifica los permisos y vuelve a activar el monitoreo.";
        }
    }

    private static bool EsArchivoOcr(string ruta) => LecturaDocumentoFiscalService.EsCompatible(ruta);

    private bool AsegurarCarpetas()
    {
        try
        {
            Directory.CreateDirectory(_carpeta607);
            Directory.CreateDirectory(_carpeta606);
            Directory.CreateDirectory(_carpetaEntrada);
            Directory.CreateDirectory(_carpetaProcesadas);
            Directory.CreateDirectory(_carpetaErrores);
            Directory.CreateDirectory(_carpetaBorradores);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron crear las carpetas DGII.");
            EstadoLabel.Text = "❌ Error: No se pudo crear la carpeta. Verifica los permisos.";
            return false;
        }
    }

    private void MostrarResultado(string reporte, int registros, string ruta)
    {
        var ahora = DateTime.Now;
        EstadoLabel.Text = $"✅ Borrador interno {reporte} generado; no es un formato oficial validado para presentar a la DGII.\nRegistros incluidos: {registros}\nGuardado en: {ruta}\nFecha y hora: {ahora:dd/MM/yyyy HH:mm:ss}";
    }

    private async void OnSeleccionarImpresoraClicked(object? sender, EventArgs e)
    {
        try { await _impresion.SeleccionarImpresoraAsync(this); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo seleccionar la impresora para los reportes DGII.");
            await DisplayAlertAsync("Impresoras", ex.Message, "Aceptar");
        }
    }

    private async void OnConsultar606Clicked(object? sender, EventArgs e) => await ConsultarLibroAsync("606");
    private async void OnConsultar607Clicked(object? sender, EventArgs e) => await ConsultarLibroAsync("607");

    private async Task ConsultarLibroAsync(string tipo)
    {
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para consultar los libros fiscales.");
            var desde = (PeriodoDesdePicker.Date ?? DateTime.Today).Date;
            var hasta = (PeriodoHastaPicker.Date ?? DateTime.Today).Date;
            if (hasta < desde) throw new InvalidOperationException("La fecha final del período no puede ser anterior a la fecha inicial.");
            var turnoId = TurnoFiltroPicker.SelectedIndex > 0 && TurnoFiltroPicker.SelectedIndex <= _turnosFiltro.Count
                ? _turnosFiltro[TurnoFiltroPicker.SelectedIndex - 1].Id : null;
            var inicioLocal = DateTime.SpecifyKind(desde, DateTimeKind.Local);
            var finLocal = DateTime.SpecifyKind(hasta.AddDays(1), DateTimeKind.Local);
            var registros = await _db.ObtenerRegistrosFiscalesAsync(uid, tipo == "606" ? "Compra" : "Venta", inicioLocal, finLocal, turnoId);
            RegistrosFiscalesLista.ItemsSource = registros;
            RegistrosFiscalesLista.IsVisible = registros.Count > 0;
            EstadoLabel.Text = $"Libro {tipo}: {registros.Count} registro(s); {registros.Count(registro => registro.EstadoRevision.Contains("Pendiente", StringComparison.OrdinalIgnoreCase))} requieren revisión.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo consultar el libro fiscal {Tipo}.", tipo);
            EstadoLabel.Text = $"No se pudo consultar el libro {tipo}: {ex.Message}";
        }
    }

    private static string CrearJsonRegistroFiscal(RegistroFiscal registro)
    {
        static decimal? Monto(string valor) => decimal.TryParse(valor, NumberStyles.Number, CultureInfo.InvariantCulture, out var monto) ? monto : null;
        var clasificacionPendiente = registro.TipoBienServicio.Contains("Pendiente", StringComparison.OrdinalIgnoreCase) || registro.EstadoRevision.Contains("Importado", StringComparison.OrdinalIgnoreCase);
        var exento = registro.EstadoRevision.Contains("Importado", StringComparison.OrdinalIgnoreCase) ? null : Monto(registro.MontoExento);
        var formaPago = string.IsNullOrWhiteSpace(registro.MetodoPago) ? null : registro.MetodoPago;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            rnc_cedula = registro.RncContraparte, ncf = registro.Ncf,
            ncf_modificado_o_afectado = string.IsNullOrWhiteSpace(registro.NcfModificado) ? null : registro.NcfModificado,
            tipo_gasto_o_ingreso = registro.TipoIngresoGasto,
            fecha_comprobante = registro.FechaComprobante.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            fecha_pago = (string?)null,
            monto_gravado_bienes = clasificacionPendiente ? null : Monto(registro.MontoGravadoBienes),
            monto_gravado_servicios = clasificacionPendiente ? null : Monto(registro.MontoGravadoServicios),
            monto_exento = exento, total_monto_facturado = Monto(registro.TotalFacturado),
            itbis_facturado = Monto(registro.ItbisFacturado), itbis_retenido = Monto(registro.ItbisRetenido),
            isr_retenido = Monto(registro.IsrRetenido), otros_impuestos = Monto(registro.OtrosImpuestos),
            propina_legal = Monto(registro.PropinaLegal), forma_pago = formaPago
        });
    }

    private async void OnGenerar606Clicked(object? sender, EventArgs e)
    {
        if (!AsegurarCarpetas()) return;
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase;
            if (string.IsNullOrWhiteSpace(uid))
            {
                EstadoLabel.Text = "⚠️ Inicia sesión para generar el reporte.";
                return;
            }
            var facturas = (await _db.GetFacturasPorOperacionAsync(uid, "Compra"))
                .Where(f => !f.Anulada && f.Estado != "Guardada" && !string.IsNullOrWhiteSpace(f.NCF))
                .OrderBy(f => f.Fecha)
                .ToList();
            var ajustes = await _db.ObtenerAjustesAsync(uid);
            await GenerarBorradorOcrAsync("606", ajustes.RncCompania, facturas, _carpeta606);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo generar el reporte 606.");
            EstadoLabel.Text = "❌ No se pudo generar el reporte 606. Inténtalo de nuevo.";
        }
    }

    private async void OnGenerar607Clicked(object? sender, EventArgs e)
    {
        if (!AsegurarCarpetas()) return;
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase;
            if (string.IsNullOrWhiteSpace(uid))
            {
                EstadoLabel.Text = "⚠️ Inicia sesión para generar el reporte.";
                return;
            }

            var facturas = (await _db.GetFacturasPorOperacionAsync(uid, "Venta"))
                .Where(f => !f.Anulada && f.Estado != "Guardada" && !string.IsNullOrWhiteSpace(f.NCF))
                .OrderBy(f => f.Fecha)
                .ToList();
            var ajustes = await _db.ObtenerAjustesAsync(uid);
            await GenerarBorradorOcrAsync("607", ajustes.RncCompania, facturas, _carpeta607);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo generar el reporte 607.");
            EstadoLabel.Text = "❌ No se pudo generar el reporte 607. Inténtalo de nuevo.";
        }
    }

    private async Task GenerarBorradorOcrAsync(string tipo, string rncCompania, List<Factura> facturas, string carpeta)
    {
        var desde = (PeriodoDesdePicker.Date ?? DateTime.Today).Date;
        var hasta = (PeriodoHastaPicker.Date ?? DateTime.Today).Date;
        if (hasta < desde) throw new InvalidOperationException("La fecha final del período no puede ser anterior a la fecha inicial.");
        var turnoId = TurnoFiltroPicker.SelectedIndex > 0 && TurnoFiltroPicker.SelectedIndex <= _turnosFiltro.Count
            ? _turnosFiltro[TurnoFiltroPicker.SelectedIndex - 1].Id : null;
        var desdeLocal = DateTime.SpecifyKind(desde, DateTimeKind.Local);
        var hastaLocal = DateTime.SpecifyKind(hasta.AddDays(1), DateTimeKind.Local);
        facturas = facturas.Where(factura => factura.Fecha.ToLocalTime().Date >= desde && factura.Fecha.ToLocalTime().Date <= hasta &&
            (string.IsNullOrWhiteSpace(turnoId) || factura.TurnoId == turnoId)).ToList();
        var operacion = tipo == "606" ? "Compra" : "Venta";
        var registrosFiscales = await _db.ObtenerRegistrosFiscalesAsync(_auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para generar reportes."), operacion, desdeLocal, hastaLocal, turnoId);
        var filas = facturas.Select(f => new RegistroReporteFiscalOcr(f.Id.ToString(), f.NCF, f.ClienteRnc, f.BorradorFiscalJson, f.ClasificacionOcrSupuesta)).ToList();
        var identidadesOcr = facturas.Where(f => !string.IsNullOrWhiteSpace(f.BorradorFiscalJson))
            .Select(f => $"{f.NCF}|{f.ClienteRnc}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        filas.AddRange(registrosFiscales.Where(registro => identidadesOcr.Add($"{registro.Ncf}|{registro.RncContraparte}"))
            .Select(registro => new RegistroReporteFiscalOcr(registro.Id, registro.Ncf, registro.RncContraparte, CrearJsonRegistroFiscal(registro))));
        var reporte = ReporteFiscalOcrService.Crear(tipo, rncCompania, filas);
        if (reporte.Registros.Count == 0)
        {
            EstadoLabel.Text = $"No hay registros para el período y turno seleccionados. Excluidos: {string.Join(", ", reporte.Excluidos)}";
            return;
        }
        var baseNombre = $"{tipo}_{DateTime.Now:yyyy-MM-dd_HHmmssfff}";
        var ruta = Path.Combine(carpeta, $"{baseNombre}.json");
        var json = System.Text.Json.JsonSerializer.Serialize(reporte, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(ruta, json, new UTF8Encoding(false));
        var rutaPdf = Path.Combine(carpeta, $"{baseNombre}.pdf");
        try
        {
            await new ReporteFiscalPdfService().CrearAsync(reporte, rutaPdf);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "El borrador {Tipo} se guardó como JSON, pero no se pudo generar su PDF imprimible.", tipo);
            MostrarResultado(tipo, reporte.Registros.Count, ruta);
            EstadoLabel.Text += "\nNo se pudo generar el PDF imprimible. Revisa la configuración y los registros.";
            return;
        }
        MostrarResultado(tipo, reporte.Registros.Count, ruta);
        EstadoLabel.Text += $"\nPDF interno imprimible: {rutaPdf}";
        EstadoLabel.Text += $"\nRegistros con campos pendientes: {reporte.Registros.Count(f => f.Pendientes.Length > 0)}. Los valores predeterminados están señalados como supuestos.";
        if (reporte.Excluidos.Count > 0)
            EstadoLabel.Text += $"\nExcluidas sin JSON asociado: {string.Join(", ", reporte.Excluidos)}";
    }

    private async void OnImprimir606Clicked(object? sender, EventArgs e) => await AbrirUltimoBorradorPdfAsync(_carpeta606, "606");
    private async void OnImprimir607Clicked(object? sender, EventArgs e) => await AbrirUltimoBorradorPdfAsync(_carpeta607, "607");

    private async Task AbrirUltimoBorradorPdfAsync(string carpeta, string tipo)
    {
        try
        {
            var ruta = Directory.EnumerateFiles(carpeta, $"{tipo}_*.pdf").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (ruta is null)
            {
                EstadoLabel.Text = $"No hay borradores PDF {tipo} para imprimir. Genera uno primero.";
                return;
            }
            await _impresion.AbrirVistaPreviaAsync(ruta, $"Borrador interno {tipo}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir el último borrador PDF {Tipo}.", tipo);
            EstadoLabel.Text = "No se pudo abrir la vista previa del borrador PDF.";
        }
    }

    private async void OnProcesarCarpetaClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        await ProcesarCarpetaAsync(null, CancellationToken.None);
#else
        await DisplayAlertAsync("OCR no disponible", "El procesamiento OCR de facturas está disponible únicamente en Windows.", "Aceptar");
#endif
    }

    private async Task ProcesarCarpetaAsync(List<string>? archivosEstables, CancellationToken cancelacion)
    {
        if (_procesando || !AsegurarCarpetas()) return;
        _procesando = true;
        ProcesarButton.IsEnabled = false;
        var ventas = 0;
        var compras = 0;
        var rechazadas = 0;
        var duplicadas = 0;
        var borradoresGenerados = 0;
        var borradoresPendientes = 0;
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase;
            if (string.IsNullOrWhiteSpace(uid))
            {
                if (archivosEstables is not null) DetenerMonitoreo();
                EstadoLabel.Text = "⚠️ Inicia sesión para procesar facturas.";
                return;
            }
            if (!_validacionFiscal.EstaConfigurado)
            {
                if (archivosEstables is not null) DetenerMonitoreo();
                EstadoLabel.Text = "⚠️ Configura DGII_OLLAMA_MODEL con un modelo instalado y ejecuta Ollama antes de procesar.";
                return;
            }
            var archivosEntrada = archivosEstables ?? Directory.EnumerateFiles(_carpetaEntrada).ToList();
            var noCompatibles = archivosEntrada.Where(ruta => !EsArchivoOcr(ruta)).Select(Path.GetFileName).ToList();
            var archivos = archivosEntrada.Where(EsArchivoOcr).ToList();
            if (archivos.Count == 0)
            {
                EstadoLabel.Text = $"No hay archivos compatibles (PDF, JPG, PNG, DOCX, XLSX o CSV) para procesar.\nCarpeta: {_carpetaEntrada}";
                if (noCompatibles.Count > 0) EstadoLabel.Text += $"\nFormatos no compatibles, sin modificar: {string.Join(", ", noCompatibles)}";
                return;
            }

            var ajustes = await _db.ObtenerAjustesAsync(uid);
            var proveedores = await _proveedores.CargarAsync();
            var errores = new List<string>();
            var resumenesMoneda = new List<string>();
            var pendientes = 0;
            for (var indice = 0; indice < archivos.Count; indice++)
            {
                var archivo = archivos[indice];
                try
                {
                    cancelacion.ThrowIfCancellationRequested();
                    var texto = await _lector.ExtraerTextoAsync(archivo, cancelacion);
                    cancelacion.ThrowIfCancellationRequested();
                    AnalisisMonedaOcr analisis;
                    try { analisis = AnalizarMoneda(texto); }
                    catch (InvalidDataException ex)
                    {
                        pendientes++;
                        errores.Add($"{Path.GetFileName(archivo)}: {ex.Message} Permanece en la carpeta de entrada para revisión.");
                        _logger.LogWarning(ex, "Moneda o tasa pendiente de revisión para {Archivo}.", Path.GetFileName(archivo));
                        if (archivosEstables is not null) DetenerMonitoreo();
                        continue;
                    }
                    MostrarTasa(analisis);
                    var resumenMoneda = FacturaOcrParser.ResumenMoneda(analisis);
                    resumenesMoneda.Add($"{Path.GetFileName(archivo)}:\n{resumenMoneda}");
                    EstadoLabel.Text = $"Procesando {Path.GetFileName(archivo)}\n{resumenMoneda}";
                    if (!FacturaOcrParser.TryParse(texto, ajustes.RncCompania, out var documento, out var motivo, archivo) || documento is null)
                    {
                        _logger.LogWarning("No se pudo interpretar {Archivo}: {Motivo}", Path.GetFileName(archivo), motivo);
                        RegistrarRechazo(archivo, motivo);
                        MoverArchivo(archivo, _carpetaErrores);
                        rechazadas++;
                        errores.Add($"{Path.GetFileName(archivo)}: {motivo}");
                        continue;
                    }

                    bool validado;
                    try
                    {
                        validado = await _validacionFiscal.ValidarAsync(texto, archivo, documento, cancelacion);
                    }
                    catch (OperationCanceledException) when (cancelacion.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        pendientes += archivos.Count - indice;
                        if (archivosEstables is not null) DetenerMonitoreo();
                        _logger.LogWarning(ex, "No se pudo validar la factura con Ollama; queda pendiente.");
                        errores.Add($"{Path.GetFileName(archivo)}: no se pudo validar con Ollama; verifica el servidor y el modelo. Permanece pendiente.");
                        break;
                    }
                    if (!validado)
                    {
                        pendientes += archivos.Count - indice;
                        if (archivosEstables is not null) DetenerMonitoreo();
                        errores.Add($"{Path.GetFileName(archivo)}: Ollama detectó datos faltantes o discrepantes; requiere revisión.");
                        break;
                    }
                    cancelacion.ThrowIfCancellationRequested();

                    BorradorFiscal606607 borradorFiscal;
                    try
                    {
                        borradorFiscal = await _borradorFiscal.ExtraerAsync(texto, documento, cancelacion);
                    }
                    catch (OperationCanceledException) when (cancelacion.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        borradoresPendientes++;
                        pendientes += archivos.Count - indice;
                        if (archivosEstables is not null) DetenerMonitoreo();
                        _logger.LogWarning(ex, "No se pudo crear el borrador JSON 606/607 para {Archivo}.", Path.GetFileName(archivo));
                        errores.Add($"{Path.GetFileName(archivo)}: no se pudo validar el JSON fiscal; el archivo permanece pendiente.");
                        break;
                    }
                    var tasaAplicada = analisis.Tasa;
                    var borradorParaGuardar = documento.MonedaOriginal == "USD"
                        ? BorradorFiscal606607OllamaService.ConvertirImportesADop(borradorFiscal, tasaAplicada)
                        : borradorFiscal;
                    borradorParaGuardar = borradorParaGuardar with { FormaPago = FacturaOcrParser.LeerFormaPagoExplicita(texto) };
                    var jsonBorrador = System.Text.Json.JsonSerializer.Serialize(borradorParaGuardar, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                    var totalDop = documento.MonedaOriginal == "USD"
                        ? FacturaOcrParser.ConvertirUSD_DOP(documento.MontoOriginal, tasaAplicada)
                        : documento.MontoOriginal;
                    var itbisDop = documento.MonedaOriginal == "USD"
                        ? FacturaOcrParser.ConvertirUSD_DOP(documento.Itbis, tasaAplicada)
                        : documento.Itbis;
                    var subtotalDop = documento.MonedaOriginal == "USD"
                        ? FacturaOcrParser.ConvertirUSD_DOP(documento.Subtotal, tasaAplicada)
                        : documento.Subtotal;
                    var rncContraparte = documento.TipoOperacion == "Compra" ? documento.RncEmisor : documento.RncContraparte;
                    var nombreContraparte = documento.NombreContraparte;
                    if (string.IsNullOrWhiteSpace(nombreContraparte) && proveedores.TryGetValue(rncContraparte, out var proveedorGuardado))
                        nombreContraparte = proveedorGuardado.RazonSocial;
                    if (string.IsNullOrWhiteSpace(nombreContraparte))
                        nombreContraparte = documento.TipoOperacion == "Compra" ? "Proveedor importado (OCR)" : "Cliente importado (OCR)";
                    var factura = new Factura
                    {
                        Id = Guid.NewGuid(), NCF = documento.Ncf, TipoNCF = documento.Ncf[..3], TipoOperacion = documento.TipoOperacion,
                        ClienteId = Guid.Empty, ClienteNombre = nombreContraparte, ClienteRnc = rncContraparte,
                        Fecha = documento.Fecha.ToUniversalTime(), Subtotal = subtotalDop, Itbis = itbisDop, Total = totalDop,
                        MontoOriginal = documento.MontoOriginal, ItbisOriginal = documento.Itbis,
                        MonedaOriginal = documento.MonedaOriginal, TasaCambio = tasaAplicada,
                        RutaArchivoOrigen = archivo, BorradorFiscalJson = jsonBorrador, ClasificacionOcrSupuesta = documento.ClasificacionSupuesta, UsuarioId = uid, Estado = "Emitida", EstadoPago = "Pendiente",
                        Items = new List<ItemFactura>
                        {
                            new() { Descripcion = "Importado por OCR; verificar clasificación fiscal", Cantidad = 1, PrecioUnitario = subtotalDop, SujetoImpuesto = itbisDop > 0 }
                        }
                    };
                    var nueva = await _db.GuardarFacturaImportadaAsync(factura);
                    if (nueva)
                    {
                        if (documento.TipoOperacion == "Venta") ventas++;
                        else compras++;
                        if (documento.ClasificacionSupuesta)
                            errores.Add($"{Path.GetFileName(archivo)}: {documento.TipoOperacion} supuesta usando el primer RNC como emisor; revisa la clasificación.");
                        if (borradorFiscal is not null)
                        {
                            var rutaBorrador = Path.Combine(_carpetaBorradores, $"{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.json");
                            try
                            {
                                await File.WriteAllTextAsync(rutaBorrador, jsonBorrador, new UTF8Encoding(false));
                                await File.WriteAllTextAsync(Path.ChangeExtension(rutaBorrador, ".txt"), $"Archivo: {Path.GetFileName(archivo)}{Environment.NewLine}NCF: {documento.Ncf}{Environment.NewLine}{resumenMoneda}{Environment.NewLine}", new UTF8Encoding(false));
                                borradoresGenerados++;
                            }
                            catch (Exception ex)
                            {
                                borradoresPendientes++;
                                _logger.LogError(ex, "No se pudo guardar el borrador JSON o el registro de moneda para {Archivo}.", Path.GetFileName(archivo));
                                errores.Add($"{Path.GetFileName(archivo)}: la factura se importó, pero el borrador JSON o el registro de moneda no pudo guardarse: {ex.Message}");
                            }
                        }
                    }
                    else duplicadas++;

                    if (documento.TipoOperacion == "Compra" && !string.IsNullOrWhiteSpace(documento.NombreContraparte))
                    {
                        proveedores[rncContraparte] = new ProveedorOcr(rncContraparte, documento.NombreContraparte.Trim(), documento.DireccionEmisor, documento.TelefonoEmisor);
                        await _proveedores.GuardarAsync(proveedores);
                    }

                    MoverArchivo(archivo, _carpetaProcesadas);
                }
                catch (VisionOllamaException ex)
                {
                    pendientes += archivos.Count - indice;
                    _logger.LogWarning(ex, "Ollama visión no pudo transcribir {Archivo}; queda pendiente.", Path.GetFileName(archivo));
                    errores.Add($"{Path.GetFileName(archivo)}: {ex.Message}");
                    if (archivosEstables is not null) DetenerMonitoreo();
                    break;
                }
                catch (HttpRequestException ex)
                {
                    pendientes += archivos.Count - indice;
                    if (archivosEstables is not null) DetenerMonitoreo();
                    _logger.LogWarning(ex, "Ollama visión no está disponible para {Archivo}; se conservará para reintentar.", Path.GetFileName(archivo));
                    errores.Add($"{Path.GetFileName(archivo)}: {ex.Message}");
                    break;
                }
                catch (OperationCanceledException) when (cancelacion.IsCancellationRequested)
                {
                    pendientes += archivos.Count - indice;
                    errores.Add("Monitoreo detenido; los archivos pendientes permanecen en la carpeta de entrada.");
                    break;
                }
                catch (TaskCanceledException ex)
                {
                    pendientes += archivos.Count - indice;
                    if (archivosEstables is not null) DetenerMonitoreo();
                    _logger.LogWarning(ex, "La solicitud de lectura de {Archivo} excedió el tiempo de espera; se conservará para reintentar.", Path.GetFileName(archivo));
                    errores.Add($"{Path.GetFileName(archivo)}: se agotó el tiempo de espera de Ollama.");
                    break;
                }
                catch (InvalidDataException ex)
                {
                    _logger.LogWarning(ex, "No se pudo leer o convertir {Archivo}: {Motivo}", Path.GetFileName(archivo), ex.Message);
                    errores.Add($"{Path.GetFileName(archivo)}: {ex.Message}");
                    try
                    {
                        RegistrarRechazo(archivo, ex.Message);
                        MoverArchivo(archivo, _carpetaErrores);
                        rechazadas++;
                    }
                    catch (Exception moverEx)
                    {
                        _logger.LogError(moverEx, "No se pudo registrar o mover {Archivo} a la carpeta de errores.", Path.GetFileName(archivo));
                        errores.Add($"{Path.GetFileName(archivo)}: no se pudo registrar o mover; permanece en Para procesar.");
                        pendientes++;
                        if (archivosEstables is not null) DetenerMonitoreo();
                        break;
                    }
                }
                catch (Exception ex)
                {
                    pendientes += archivos.Count - indice;
                    if (archivosEstables is not null) DetenerMonitoreo();
                    _logger.LogError(ex, "Falló el OCR o la importación de {Archivo}; se conservará para reintentar.", Path.GetFileName(archivo));
                    errores.Add($"{Path.GetFileName(archivo)}: falló el procesamiento y sigue en Para procesar.");
                    break;
                }
            }

            var resumen = ventas == 0 && compras == 0 && duplicadas == 0
                ? rechazadas > 0 ? "❌ No se procesaron facturas válidas." : "⚠️ No se completó el procesamiento."
                : $"✅ Procesamiento completado — {ventas} facturas de venta, {compras} facturas de compra.";
            if (duplicadas > 0) resumen += $"\nDuplicadas: {duplicadas}.";
            if (borradoresGenerados > 0) resumen += $"\nBorradores JSON auditables generados: {borradoresGenerados}. Carpeta: {_carpetaBorradores}";
            if (borradoresPendientes > 0) resumen += $"\nBorradores JSON no generados o no guardados: {borradoresPendientes}; revisa los mensajes.";
            if (rechazadas > 0) resumen += $"\nNo procesadas: {rechazadas}. Revisa Cargas OCR\\Errores y los mensajes indicados.";
            if (pendientes > 0) resumen += $"\nPendientes de reintento: {pendientes}. Los archivos siguen en Para procesar.";
            if (noCompatibles.Count > 0) resumen += $"\nFormatos no compatibles, sin modificar: {string.Join(", ", noCompatibles)}";
            if (errores.Count > 0) resumen += $"\n{string.Join("\n", errores.Take(5))}";
            if (resumenesMoneda.Count > 0) resumen += $"\n{string.Join("\n\n", resumenesMoneda)}";
            EstadoLabel.Text = $"{resumen}\nFecha y hora: {DateTime.Now:dd/MM/yyyy HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo procesar la carpeta OCR.");
            EstadoLabel.Text = "❌ No se pudo procesar la carpeta de facturas. Revisa los permisos y vuelve a intentarlo.";
        }
        finally
        {
            _procesando = false;
            ProcesarButton.IsEnabled = true;
        }
    }

    private async void OnExportarCsvClicked(object? sender, EventArgs e)
    {
        if (!AsegurarCarpetas()) return;
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase;
            if (string.IsNullOrWhiteSpace(uid))
            {
                EstadoLabel.Text = "⚠️ Inicia sesión para exportar las facturas OCR.";
                return;
            }

            var facturas = new List<Factura>();
            facturas.AddRange((await _db.GetFacturasPorOperacionAsync(uid, "Venta")).Where(f => !string.IsNullOrWhiteSpace(f.RutaArchivoOrigen)));
            facturas.AddRange((await _db.GetFacturasPorOperacionAsync(uid, "Compra")).Where(f => !string.IsNullOrWhiteSpace(f.RutaArchivoOrigen)));
            if (!TryLeerTasaCambio(TasaCambioEntry.Text, out var tasaCambio))
            {
                EstadoLabel.Text = "⚠️ Introduce una tasa de cambio USD a DOP mayor que cero.";
                return;
            }

            var lineas = new List<string>
            {
                "Comprobante,RNC,Proveedor,Fecha,Moneda,SubtotalDOP,ITBIS,TotalDOP,MontoUSD,TasaCambio,ArchivoOrigen"
            };
            foreach (var factura in facturas.OrderBy(f => f.Fecha))
            {
                var tasaFactura = factura.TasaCambio > 0 ? factura.TasaCambio : tasaCambio;
                var montoUsd = factura.MonedaOriginal == "USD"
                    ? factura.MontoOriginal
                    : FacturaOcrParser.ConvertirDOP_USD(factura.Total, tasaFactura);
                lineas.Add(string.Join(",", new[]
                {
                    factura.NCF, factura.ClienteRnc, factura.ClienteNombre,
                    factura.Fecha.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    factura.MonedaOriginal, factura.Subtotal.ToString("F2", CultureInfo.InvariantCulture),
                    factura.Itbis.ToString("F2", CultureInfo.InvariantCulture), factura.Total.ToString("F2", CultureInfo.InvariantCulture),
                    montoUsd.ToString("F2", CultureInfo.InvariantCulture), tasaFactura.ToString("F2", CultureInfo.InvariantCulture), factura.RutaArchivoOrigen
                }.Select(CsvCampo)));
            }

            var ruta = Path.Combine(_raiz, "facturas_procesadas.csv");
            await File.WriteAllLinesAsync(ruta, lineas, new UTF8Encoding(true));
            EstadoLabel.Text = $"✅ CSV exportado. Facturas: {facturas.Count}\nGuardado en: {ruta}\nFecha y hora: {DateTime.Now:dd/MM/yyyy HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo exportar las facturas OCR a CSV.");
            EstadoLabel.Text = "❌ No se pudo guardar facturas_procesadas.csv. Verifica los permisos.";
        }
    }

    private void RegistrarRechazo(string archivo, string motivo)
    {
        static string UnaLinea(string valor) => valor.Replace('\r', ' ').Replace('\n', ' ');
        var registro = $"{DateTimeOffset.Now:O} | {UnaLinea(Path.GetFileName(archivo))} | {UnaLinea(motivo)}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(_raiz, "registro-procesamiento.log"), registro, new UTF8Encoding(false));
    }

    private void MoverArchivo(string archivo, string carpetaDestino)
    {
        var nombre = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Path.GetFileName(archivo)}";
        var destino = Path.Combine(carpetaDestino, nombre);
        var indice = 1;
        while (File.Exists(destino))
            destino = Path.Combine(carpetaDestino, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{indice++}_{Path.GetFileName(archivo)}");
        File.Move(archivo, destino);
    }

    private static string CsvCampo(string? valor)
    {
        var campo = valor ?? string.Empty;
        return $"\"{campo.Replace("\"", "\"\"")}\"";
    }

    private static bool TryLeerTasaCambio(string? texto, out decimal tasa) =>
        decimal.TryParse(texto?.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out tasa) && tasa > 0;

    private void OnVerReportesClicked(object? sender, EventArgs e)
    {
        if (!Directory.Exists(_raiz))
        {
            EstadoLabel.Text = "⚠️ La carpeta aún no existe. Genera un reporte primero.";
            return;
        }
#if WINDOWS
        try
        {
            using var proceso = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _raiz,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la carpeta de reportes DGII.");
            EstadoLabel.Text = "❌ No se pudo abrir la carpeta de reportes.";
        }
#else
        EstadoLabel.Text = "La apertura de la carpeta está disponible únicamente en Windows.";
#endif
    }
}
