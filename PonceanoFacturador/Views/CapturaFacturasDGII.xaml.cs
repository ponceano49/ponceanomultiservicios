using System.Diagnostics;
using System.Globalization;
#if WINDOWS
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;
using CvPoint2f = OpenCvSharp.Point2f;
using CvSize = OpenCvSharp.Size;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class CapturaFacturasDGII : ContentPage
{
    private readonly AuthService _auth;
    private readonly CapturaFacturasDgiiService _capturas;
    private readonly TesseractOcrService _tesseract;
    private readonly DatabaseService _db;
    private readonly ListaLocalRncService _listaLocal;
    private readonly RncContribuyentesService _rncContribuyentes;
    private readonly Dictionary<string, string> _rncListaLocal = new(StringComparer.Ordinal);
    private bool _listaLocalDisponible;
    private bool _padronDisponible;
    private bool _actualizandoDesdeListado;
    private readonly IServiceProvider _services;
    private readonly ILogger<CapturaFacturasDGII> _logger;
    private readonly Queue<FacturaPendiente> _cola = new();
    private string? _archivo;
    private string? _rutaFacturaTemporal;
    private string _monedaActual = "DOP";
    private bool _procesandoCola;
    private bool _loteActivo;
    private bool _proveedorValidado;
    private bool _plantillaConfirmadaPorOcr;
    private int _duplicadasLote;
    private int _pendientesLote;
    private int _totalArchivos;
    private int _numeroProcesado;
    private string? _idEdicion;
    private CapturaFacturaRevision? _revisionActual;
    private CapturaFacturaDgii? _facturaEnRevision;
    private string _textoOcrActual = string.Empty;
    private string _rncProveedor = string.Empty;
    private string _razonSocialProveedor = string.Empty;
    private string _ncfComprobante = string.Empty;
    private string _subtotal = string.Empty;
    private string _itbis = string.Empty;
    private string _total = string.Empty;
    private bool _mostrandoLecturaOcr;
    private bool _datosCliente;
    private bool _esperandoRevision;
    private bool _capturaCompra606Actual;
    private string? _rutaFotoEscaneadaActual;
    private readonly List<CapturaFacturaDgii> _exitos = new();
    private readonly List<CapturaFacturaRevision> _revisiones = new();
    private readonly List<string> _duplicadosDetalle = new();
    private readonly HashSet<string> _pendientesLoteIds = new(StringComparer.Ordinal);
    private sealed record FacturaPendiente(string Nombre, Func<Task<Stream>> AbrirAsync, bool RequiereRevisionManual606 = false, string? RutaImagenPermanente = null);
    private byte[]? _fotoDocumentoCongelada;
    private int _solicitarCongelarDocumento;

    public CapturaFacturasDGII(AuthService auth, CapturaFacturasDgiiService capturas, TesseractOcrService tesseract, DatabaseService db, ListaLocalRncService listaLocal, RncContribuyentesService rncContribuyentes, IServiceProvider services, ILogger<CapturaFacturasDGII> logger)
    {
        InitializeComponent();
        _auth = auth;
        _capturas = capturas;
        _tesseract = tesseract;
        _db = db;
        _listaLocal = listaLocal;
        _rncContribuyentes = rncContribuyentes;
        _services = services;
        _logger = logger;
        MonedaPicker.SelectedIndex = 0;
        CapturarCompraButton.IsVisible = OperatingSystem.IsWindows();
        TipoCompraPicker.ItemsSource = new[]
        {
            "01 - Gastos de personal", "02 - Trabajos, suministros y servicios", "03 - Arrendamientos",
            "04 - Gastos de activos fijos", "05 - Gastos de representación", "06 - Otras deducciones admitidas",
            "07 - Gastos financieros", "08 - Gastos extraordinarios", "09 - Compras para costo de venta",
            "10 - Adquisiciones de activos", "11 - Gastos de seguros"
        };
        BaseCompraPicker.ItemsSource = new[] { "Bienes", "Servicios" };
        MetodoPagoCompraPicker.ItemsSource = new[] { "Efectivo", "Cheque/Transferencia/Depósito", "Tarjeta", "Crédito", "Permuta", "Otro" };
        foreach (var campo in new[] { RncEntry, RazonEntry, NcfEntry, SubtotalEntry, ItbisEntry, TotalEntry })
            campo.TextChanged += (_, _) => ActualizarAdvertenciasCampos();
        RncEntry.TextChanged += (_, _) => { _proveedorValidado = false; ActualizarBotonRnc(); };
        RazonEntry.TextChanged += (_, _) => { _proveedorValidado = false; ActualizarBotonRnc(); };
        RncEntry.Unfocused += OnRncUnfocused;
        RazonEntry.Unfocused += OnRazonUnfocused;
    }

    private string UsuarioId => _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para utilizar la captura.");

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            var usuario = _auth.CurrentUser ?? throw new InvalidOperationException("Inicia sesión para utilizar la captura.");
            SesionLabel.Text = $"Sesión: {usuario.Nombre} | Usuario: {usuario.Correo} | Rol: no disponible";
            await ActualizarAsync();
            await CargarListaLocalAsync();
            try
            {
                var progresoPadron = new Progress<string>(mensaje => MostrarEstadoRnc(mensaje, !mensaje.StartsWith("⚠️", StringComparison.Ordinal)));
                await _rncContribuyentes.InicializarAsync(progresoPadron);
                _padronDisponible = true;
                await SincronizarProveedorConPadronAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo inicializar el padrón local RNC_CONTRIBUYENTES.");
                _padronDisponible = false;
                MostrarEstadoRnc($"No se pudo cargar el padrón local RNC_CONTRIBUYENTES: {ex.Message}", false);
            }
#if WINDOWS
            ActivarArrastreArchivos();
#else
            ArchivoLabel.Text = "La selección y lectura OCR de facturas está disponible en Windows; aquí puedes guardar datos manualmente.";
#endif
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron cargar las capturas DGII.");
            EstadoLabel.Text = $"No se pudieron cargar las capturas: {ex.Message}";
        }
    }

    private async Task ActualizarAsync()
    {
        var facturas = await _capturas.ListarAsync(UsuarioId);
        FacturasLista.ItemsSource = facturas;
        EstadoLabel.Text = $"Sistema listo. {facturas.Count} facturas cargadas.";
    }

    private async Task CargarListaLocalAsync()
    {
        try
        {
            var lista = await _listaLocal.CargarAsync();
            _rncListaLocal.Clear();
            foreach (var par in lista) _rncListaLocal.Add(par.Key, par.Value);
            _listaLocalDisponible = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo leer la lista local de RNC.");
            _listaLocalDisponible = false;
            ListaRncEstadoLabel.Text = $"No se pudo leer Lista_RNC_Proveedores.csv en los datos de la aplicación: {ex.Message}";
            ListaRncEstadoLabel.IsVisible = true;
        }
        ActualizarBotonRnc();
        ActualizarAdvertenciasCampos();
    }

    private void ActualizarBotonRnc()
    {
        var rnc = ListaLocalRncService.NormalizarRnc(RncEntry.Text);
        GuardarRncButton.IsVisible = _listaLocalDisponible && ListaLocalRncService.EsRncValido(rnc) &&
            !string.IsNullOrWhiteSpace(RazonEntry.Text) && !_rncListaLocal.ContainsKey(rnc);
    }

    private void MostrarEstadoRnc(string mensaje, bool correcto)
    {
        ListaRncEstadoLabel.Text = mensaje;
        ListaRncEstadoLabel.TextColor = Color.FromArgb(correcto ? "#267344" : "#9A5A00");
        ListaRncEstadoLabel.IsVisible = !string.IsNullOrWhiteSpace(mensaje);
    }

    private async void OnRncUnfocused(object? sender, FocusEventArgs e)
    {
        if (_procesandoCola || _actualizandoDesdeListado || string.IsNullOrWhiteSpace(RncEntry.Text)) return;
        if (!_padronDisponible)
        {
            MostrarEstadoRnc("No está disponible el padrón local RNC_CONTRIBUYENTES.", false);
            return;
        }
        var rnc = ListaLocalRncService.NormalizarRnc(RncEntry.Text);
        if (!ListaLocalRncService.EsRncValido(rnc))
        {
            MostrarEstadoRnc("⚠️ RNC no encontrado en el listado, complete la Razón Social manualmente", false);
            return;
        }
        var rncAlConsultar = RncEntry.Text;
        var razonAlConsultar = RazonEntry.Text;
        try
        {
            _actualizandoDesdeListado = true;
            var razon = await _rncContribuyentes.BuscarPorRncAsync(rnc);
            if (!string.Equals(RncEntry.Text, rncAlConsultar, StringComparison.Ordinal) || !string.Equals(RazonEntry.Text, razonAlConsultar, StringComparison.Ordinal)) return;
            if (razon is null)
            {
                MostrarEstadoRnc("⚠️ RNC no encontrado en el listado, complete la Razón Social manualmente", false);
                return;
            }
            RazonEntry.Text = razon;
            MostrarEstadoRnc("✅ Razón Social cargada automáticamente desde el listado local", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló la búsqueda del RNC {Rnc} en el padrón local.", rnc);
            MostrarEstadoRnc($"No se pudo consultar el padrón local: {ex.Message}", false);
        }
        finally { _actualizandoDesdeListado = false; }
    }

    private async void OnRazonUnfocused(object? sender, FocusEventArgs e)
    {
        if (_procesandoCola || _actualizandoDesdeListado || string.IsNullOrWhiteSpace(RazonEntry.Text)) return;
        if (!_padronDisponible)
        {
            MostrarEstadoRnc("No está disponible el padrón local RNC_CONTRIBUYENTES.", false);
            return;
        }
        var rncAlConsultar = RncEntry.Text;
        var razonAlConsultar = RazonEntry.Text;
        try
        {
            _actualizandoDesdeListado = true;
            var coincidencias = await _rncContribuyentes.BuscarPorRazonSocialAsync(razonAlConsultar);
            if (!string.Equals(RncEntry.Text, rncAlConsultar, StringComparison.Ordinal) || !string.Equals(RazonEntry.Text, razonAlConsultar, StringComparison.Ordinal)) return;
            if (coincidencias.Count == 0)
            {
                MostrarEstadoRnc("⚠️ Razón Social no encontrada en el listado, complete el RNC manualmente", false);
                return;
            }
            if (coincidencias.Count > 1)
            {
                MostrarEstadoRnc("⚠️ Hay varias coincidencias para esta Razón Social; complete el RNC manualmente", false);
                return;
            }
            RncEntry.Text = coincidencias[0].Rnc;
            MostrarEstadoRnc("✅ RNC cargado automáticamente desde el listado local", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló la búsqueda de la razón social en el padrón local.");
            MostrarEstadoRnc($"No se pudo consultar el padrón local: {ex.Message}", false);
        }
        finally { _actualizandoDesdeListado = false; }
    }

    private async Task SincronizarProveedorConPadronAsync()
    {
        _proveedorValidado = false;
        if (!_padronDisponible) return;
        var rncEntrada = RncEntry.Text;
        var razonEntrada = RazonEntry.Text;
        var rnc = ListaLocalRncService.NormalizarRnc(rncEntrada);
        var razon = (razonEntrada ?? string.Empty).Trim();
        if (rnc.Length == 0 && razon.Length == 0) return;
        try
        {
            _actualizandoDesdeListado = true;
            if (ListaLocalRncService.EsRncValido(rnc))
            {
                var razonEncontrada = await _rncContribuyentes.BuscarPorRncAsync(rnc);
                if (!string.Equals(RncEntry.Text, rncEntrada, StringComparison.Ordinal) || !string.Equals(RazonEntry.Text, razonEntrada, StringComparison.Ordinal)) return;
                if (razonEncontrada is null)
                {
                    MostrarEstadoRnc("⚠️ RNC no encontrado en el listado, complete la Razón Social manualmente", false);
                    return;
                }
                if (string.IsNullOrWhiteSpace(razon))
                {
                    RazonEntry.Text = razonEncontrada;
                    _proveedorValidado = true;
                    MostrarEstadoRnc("✅ Razón Social cargada automáticamente desde el listado local", true);
                    return;
                }
                if (RncContribuyentesService.NormalizarRazonSocial(razon) == RncContribuyentesService.NormalizarRazonSocial(razonEncontrada))
                {
                    _proveedorValidado = true;
                    MostrarEstadoRnc("✅ RNC y Razón Social coinciden con el padrón local", true);
                    return;
                }
                MostrarEstadoRnc("⚠️ La Razón Social no coincide con el RNC del padrón local; verifique los datos", false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(razon))
            {
                var coincidencias = await _rncContribuyentes.BuscarPorRazonSocialAsync(razon);
                if (!string.Equals(RncEntry.Text, rncEntrada, StringComparison.Ordinal) || !string.Equals(RazonEntry.Text, razonEntrada, StringComparison.Ordinal)) return;
                if (coincidencias.Count == 1)
                {
                    RncEntry.Text = coincidencias[0].Rnc;
                    _proveedorValidado = true;
                    MostrarEstadoRnc("✅ RNC cargado automáticamente desde el listado local", true);
                }
                else if (coincidencias.Count > 1)
                    MostrarEstadoRnc("⚠️ Hay varias coincidencias para esta Razón Social; verifique el RNC", false);
                else
                    MostrarEstadoRnc("⚠️ Razón Social no encontrada en el listado, complete el RNC manualmente", false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló la validación del proveedor OCR contra el padrón local.");
            MostrarEstadoRnc($"No se pudo consultar el padrón local: {ex.Message}", false);
        }
        finally { _actualizandoDesdeListado = false; }
    }

    private async void OnGuardarRncClicked(object? sender, EventArgs e)
    {
        GuardarRncButton.IsEnabled = false;
        try
        {
            var rnc = ListaLocalRncService.NormalizarRnc(RncEntry.Text);
            var razon = (RazonEntry.Text ?? string.Empty).Trim();
            var lista = await _listaLocal.AgregarAsync(rnc, razon);
            _rncListaLocal.Clear();
            foreach (var par in lista) _rncListaLocal.Add(par.Key, par.Value);
            _listaLocalDisponible = true;
            ActualizarBotonRnc();
            ActualizarAdvertenciasCampos();
            await DisplayAlertAsync("Lista local de RNC", $"Se guardó el RNC {rnc} en Lista_RNC_Proveedores.csv.\n{_listaLocal.RutaCsv}", "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar el RNC en la lista local.");
            await DisplayAlertAsync("Lista local de RNC", $"No se pudo guardar el RNC: {ex.Message}", "Aceptar");
            await CargarListaLocalAsync();
        }
        finally { GuardarRncButton.IsEnabled = true; }
    }

    private async void OnCargarClicked(object? sender, EventArgs e)
    {
        CargarButton.IsEnabled = false;
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("La lectura Tesseract de esta aplicación está habilitada actualmente para Windows.");
            var seleccionados = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Selecciona una o varias facturas PDF o imágenes",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.WinUI] = new[] { ".pdf", ".jpg", ".jpeg", ".png" },
                    [DevicePlatform.Android] = new[] { "application/pdf", "image/jpeg", "image/png" },
                    [DevicePlatform.iOS] = new[] { "com.adobe.pdf", "public.jpeg", "public.png" },
                    [DevicePlatform.MacCatalyst] = new[] { "com.adobe.pdf", "public.jpeg", "public.png" }
                })
            });
            IEnumerable<FileResult> archivos = seleccionados ?? Array.Empty<FileResult>();
            var pendientes = archivos.Where(archivo => EsFormatoAdmitido(archivo.FileName))
                .Select(archivo => new FacturaPendiente(archivo.FileName, archivo.OpenReadAsync)).ToList();
            var rechazados = archivos.Count() - pendientes.Count;
            if (rechazados > 0) await DisplayAlertAsync("Formato no compatible", $"Se omitieron {rechazados} archivos. Solo se aceptan JPG, JPEG, PNG y PDF.", "Aceptar");
            await AgregarArchivosAsync(pendientes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron seleccionar las facturas.");
            await DisplayAlertAsync("Captura DGII", ex.Message, "Aceptar");
        }
        finally { CargarButton.IsEnabled = !_loteActivo; }
    }

    private static bool EsFormatoAdmitido(string nombre) =>
        new[] { ".pdf", ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(nombre), StringComparer.OrdinalIgnoreCase);

    private async void OnCapturarCompraClicked(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            await DisplayAlertAsync("Captura inteligente", "La captura con cámara y lectura OCR está disponible en Windows.", "Aceptar");
            return;
        }
        if (_loteActivo)
        {
            await DisplayAlertAsync("Captura inteligente", "Espera a que termine el procesamiento actual.", "Aceptar");
            return;
        }

        CapturarCompraButton.IsEnabled = false;
        CapturaInteligenteOverlay.IsVisible = true;
        VistaCamaraImage.Source = null;
        MarcoCapturaBorder.IsVisible = true;
        _fotoDocumentoCongelada = null;
        Interlocked.Exchange(ref _solicitarCongelarDocumento, 0);
        MarcoCapturaBorder.Stroke = Color.FromArgb("#FFFFFF");
        CuentaRegresivaLabel.TextColor = Color.FromArgb("#C62828");
        CuentaRegresivaLabel.Text = "10";
        EstadoCapturaInteligenteLabel.Text = "Buscando los bordes del documento…";
        FijarDocumentoButton.IsVisible = false;
        TomarFotoGuardarButton.IsVisible = false;
        ReiniciarCapturaButton.IsVisible = false;
        using var cancelacion = new CancellationTokenSource();
        _capturaCamaraCancellation = cancelacion;
        try
        {
            var bytes = await Task.Run(() => CapturarFacturaConCamaraAsync(cancelacion.Token), cancelacion.Token);
            if (bytes is null) return;
            _fotoDocumentoCongelada = bytes;
            VistaCamaraImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
            MarcoCapturaBorder.IsVisible = false;
            EstadoCapturaInteligenteLabel.Text = "✅ Imagen fijada y ajustada. Revísala antes de guardarla.";
            CuentaRegresivaLabel.Text = "✓";
            CuentaRegresivaLabel.TextColor = Color.FromArgb("#267344");
            FijarDocumentoButton.IsVisible = false;
            TomarFotoGuardarButton.IsVisible = true;
            ReiniciarCapturaButton.IsVisible = true;
        }
        catch (OperationCanceledException) when (cancelacion.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo detectar o capturar la factura de compra para el Formato 606.");
            CapturaInteligenteOverlay.IsVisible = false;
            CapturarCompraButton.IsEnabled = true;
            await DisplayAlertAsync("Captura inteligente 606", $"No se pudo iniciar o completar la captura de cámara: {ex.Message}", "Aceptar");
        }
        finally
        {
            if (ReferenceEquals(_capturaCamaraCancellation, cancelacion)) _capturaCamaraCancellation = null;
            if (!_loteActivo && !CapturaInteligenteOverlay.IsVisible) CapturarCompraButton.IsEnabled = true;
        }
    }

    private CancellationTokenSource? _capturaCamaraCancellation;

    private void OnFijarDocumentoClicked(object? sender, EventArgs e)
    {
        FijarDocumentoButton.IsEnabled = false;
        EstadoCapturaInteligenteLabel.Text = "Fijando la imagen y ajustando la perspectiva…";
        Interlocked.Exchange(ref _solicitarCongelarDocumento, 1);
    }

    private async void OnTomarFotoGuardarClicked(object? sender, EventArgs e)
    {
        if (_fotoDocumentoCongelada is not { Length: > 0 } foto) return;
        TomarFotoGuardarButton.IsEnabled = false;
        try
        {
            var ruta = await GuardarFotoEscaneadaAsync(foto);
            CapturaInteligenteOverlay.IsVisible = false;
            CapturarCompraButton.IsEnabled = !_loteActivo;
            var pendiente = new FacturaPendiente(Path.GetFileName(ruta), () => Task.FromResult<Stream>(File.OpenRead(ruta)), true, ruta);
            await AgregarArchivosAsync(new[] { pendiente });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la imagen fijada de la factura para el Formato 606.");
            await DisplayAlertAsync("Captura inteligente 606", $"No se pudo guardar la imagen: {ex.Message}", "Aceptar");
        }
        finally
        {
            TomarFotoGuardarButton.IsEnabled = true;
        }
    }

    private void OnReiniciarCapturaClicked(object? sender, EventArgs e)
    {
        _capturaCamaraCancellation?.Cancel();
        _fotoDocumentoCongelada = null;
        CapturaInteligenteOverlay.IsVisible = false;
        CapturarCompraButton.IsEnabled = true;
        OnCapturarCompraClicked(sender, e);
    }

    private void OnCancelarCapturaInteligenteClicked(object? sender, EventArgs e)
    {
        _capturaCamaraCancellation?.Cancel();
        _fotoDocumentoCongelada = null;
        CapturaInteligenteOverlay.IsVisible = false;
    }

    private async Task<byte[]?> CapturarFacturaConCamaraAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        using var camara = new VideoCapture();
        if (!camara.Open(0, VideoCaptureAPIs.DSHOW))
            throw new InvalidOperationException("No se pudo abrir la cámara. Verifica que esté conectada y que Windows permita el acceso a la cámara.");
        camara.Set(VideoCaptureProperties.FrameWidth, 1280);
        camara.Set(VideoCaptureProperties.FrameHeight, 720);
        var autofocusDisponible = camara.Set(VideoCaptureProperties.AutoFocus, 1);
        var zoomActual = camara.Get(VideoCaptureProperties.Zoom);
        var zoomDisponible = double.IsFinite(zoomActual) && zoomActual >= 0 && camara.Set(VideoCaptureProperties.Zoom, zoomActual);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (CapturaInteligenteOverlay.IsVisible)
                EstadoCapturaInteligenteLabel.Text = $"COLOQUE LA FACTURA / RECIBO DENTRO DEL MARCO · Autofoco: {(autofocusDisponible ? "disponible" : "no disponible")} · Zoom: {(zoomDisponible ? "disponible" : "no disponible")}.";
        });
        using var frame = new Mat();
        var cronometro = Stopwatch.StartNew();
        var cuadros = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!camara.Read(frame) || frame.Empty()) throw new InvalidOperationException("La cámara no entregó una imagen. Comprueba la conexión o el permiso de cámara.");
            var poligono = DetectarDocumento(frame);
            var segundosRestantes = Math.Max(0, 10 - (int)cronometro.Elapsed.TotalSeconds);
            if (Interlocked.CompareExchange(ref _solicitarCongelarDocumento, 0, 0) == 1)
            {
                using var ajustada = AjustarDocumento(frame, poligono);
                if (!Cv2.ImEncode(".jpg", ajustada, out var foto)) throw new InvalidDataException("No se pudo preparar la imagen ajustada.");
                cancellationToken.ThrowIfCancellationRequested();
                return foto.ToArray();
            }
            if (poligono is not null) Cv2.Polylines(frame, new[] { poligono }, true, Scalar.LimeGreen, 4);
            if (cuadros++ % 3 == 0)
            {
                if (!Cv2.ImEncode(".jpg", frame, out var vista)) throw new InvalidDataException("No se pudo actualizar la vista previa de la cámara.");
                var vistaCopia = vista.ToArray();
                var hayBordes = poligono is not null;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!CapturaInteligenteOverlay.IsVisible) return;
                    VistaCamaraImage.Source = ImageSource.FromStream(() => new MemoryStream(vistaCopia));
                    CuentaRegresivaLabel.Text = segundosRestantes.ToString(CultureInfo.InvariantCulture);
                    CuentaRegresivaLabel.TextColor = segundosRestantes == 0 ? Color.FromArgb("#267344") : Color.FromArgb("#C62828");
                    MarcoCapturaBorder.Stroke = Color.FromArgb("#FFFFFF");
                    EstadoCapturaInteligenteLabel.Text = hayBordes
                        ? segundosRestantes == 0 ? "✅ Bordes detectados. Presione FIJAR DOCUMENTO para recortar y corregir perspectiva." : "✅ Bordes detectados; mantenga el documento dentro de la guía."
                        : segundosRestantes == 0 ? "⚠️ Aún no se detectan bordes. Puede fijar la imagen para usar el recorte de respaldo." : "Buscando los bordes del documento…";
                    FijarDocumentoButton.IsVisible = true;
                    FijarDocumentoButton.IsEnabled = true;
                });
            }
            await Task.Delay(33, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return null;
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("La cámara para captura inteligente está habilitada actualmente para Windows.");
#endif
    }

#if WINDOWS
    private static CvPoint[]? DetectarDocumento(Mat frame)
    {
        using var gris = new Mat();
        using var suavizada = new Mat();
        using var bordes = new Mat();
        Cv2.CvtColor(frame, gris, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gris, suavizada, new CvSize(5, 5), 0);
        Cv2.Canny(suavizada, bordes, 45, 135);
        Cv2.FindContours(bordes, out CvPoint[][] contornos, out HierarchyIndex[] _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var areaMinima = frame.Width * frame.Height * 0.025;
        foreach (var contorno in contornos.OrderByDescending(contorno => Cv2.ContourArea(contorno)))
        {
            if (Cv2.ContourArea(contorno) < areaMinima) break;
            var poligono = Cv2.ApproxPolyDP(contorno, Cv2.ArcLength(contorno, true) * 0.02, true);
            if (poligono.Length != 4 || !Cv2.IsContourConvex(poligono)) continue;
            var rectangulo = Cv2.BoundingRect(poligono);
            var proporcion = rectangulo.Width / (double)rectangulo.Height;
            if (proporcion is < 0.3 or > 3.3 || rectangulo.Width < frame.Width * 0.12 || rectangulo.Height < frame.Height * 0.08) continue;
            return poligono;
        }
        return null;
    }

    private static Mat AjustarDocumento(Mat frame, CvPoint[]? poligono)
    {
        if (poligono is { Length: 4 })
        {
            var esquinas = OrdenarEsquinas(poligono);
            var ancho = (int)Math.Max(Distancia(esquinas[0], esquinas[1]), Distancia(esquinas[3], esquinas[2]));
            var alto = (int)Math.Max(Distancia(esquinas[0], esquinas[3]), Distancia(esquinas[1], esquinas[2]));
            if (ancho > 1 && alto > 1)
            {
                var destino = new[] { new CvPoint2f(0, 0), new CvPoint2f(ancho - 1, 0), new CvPoint2f(ancho - 1, alto - 1), new CvPoint2f(0, alto - 1) };
                using var transformacion = Cv2.GetPerspectiveTransform(esquinas, destino);
                var salida = new Mat();
                Cv2.WarpPerspective(frame, salida, transformacion, new CvSize(ancho, alto));
                return salida;
            }
        }

        var x = (int)(frame.Width * 0.08);
        var y = (int)(frame.Height * 0.10);
        var anchoRecorte = (int)(frame.Width * 0.84);
        var altoRecorte = (int)(frame.Height * 0.80);
        return frame[new OpenCvSharp.Rect(x, y, anchoRecorte, altoRecorte)].Clone();
    }

    private static CvPoint2f[] OrdenarEsquinas(CvPoint[] puntos)
    {
        var flotantes = puntos.Select(punto => new CvPoint2f(punto.X, punto.Y)).ToArray();
        return new[]
        {
            flotantes.MinBy(punto => punto.X + punto.Y)!,
            flotantes.MinBy(punto => punto.Y - punto.X)!,
            flotantes.MaxBy(punto => punto.X + punto.Y)!,
            flotantes.MaxBy(punto => punto.Y - punto.X)!
        };
    }

    private static double Distancia(CvPoint2f a, CvPoint2f b)
    {
        var x = a.X - b.X;
        var y = a.Y - b.Y;
        return Math.Sqrt(x * x + y * y);
    }
#endif

    private static async Task<string> GuardarFotoEscaneadaAsync(byte[] foto)
    {
        var documentos = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var carpeta = string.IsNullOrWhiteSpace(documentos)
            ? Path.Combine(FileSystem.AppDataDirectory, "Facturas_Escaneadas")
            : Path.Combine(documentos, "Facturas_Escaneadas");
        Directory.CreateDirectory(carpeta);
        var nombreBase = $"FacturaEscaneada_{DateTime.Now:yyyyMMdd_HHmmss}";
        var ruta = Path.Combine(carpeta, nombreBase + ".jpg");
        var sufijo = 2;
        while (File.Exists(ruta)) ruta = Path.Combine(carpeta, $"{nombreBase}_{sufijo++}.jpg");
        try
        {
            await File.WriteAllBytesAsync(ruta, foto);
            return ruta;
        }
        catch
        {
            if (File.Exists(ruta)) File.Delete(ruta);
            throw;
        }
    }

    private async void OnImportarCompra606Clicked(object? sender, EventArgs e)
    {
        if (!_capturaCompra606Actual || !_esperandoRevision) return;
        ImportarCompra606Button.IsEnabled = false;
        try
        {
            var factura = CrearFacturaValidada();
            if (!CorreccionFiscalMontos.TryLeer(factura.Subtotal, out var gravado) ||
                !CorreccionFiscalMontos.TryLeer(factura.Itbis, out var itbis) ||
                !CorreccionFiscalMontos.TryLeer(factura.Total, out var total) ||
                !CorreccionFiscalMontos.TryLeer(string.IsNullOrWhiteSpace(MontoExentoCompraEntry.Text) ? "0" : MontoExentoCompraEntry.Text, out var exento) ||
                gravado + exento + itbis != total)
                throw new InvalidOperationException("Verifica los montos: el total debe ser igual al monto gravado más el monto exento y el ITBIS.");
            if (TipoCompraPicker.SelectedItem is not string tipoCompra || BaseCompraPicker.SelectedItem is not string baseCompra ||
                MetodoPagoCompraPicker.SelectedItem is not string metodoPago)
                throw new InvalidOperationException("Selecciona la clasificación 606, si la base es bienes o servicios y el método de pago.");

            var codigoPago = metodoPago == "Cheque/Transferencia/Depósito" ? "Transferencia" : metodoPago;
            var bienes = baseCompra == "Bienes" ? gravado : 0m;
            var servicios = baseCompra == "Servicios" ? gravado : 0m;
            var fecha = FechaCompraPicker.Date is DateTime fechaSeleccionada
                ? fechaSeleccionada.Date
                : throw new InvalidOperationException("Selecciona la fecha del comprobante.");
            var registro = new RegistroFiscal
            {
                UsuarioId = UsuarioId,
                TipoOperacion = "Compra",
                FechaComprobante = DateTime.SpecifyKind(fecha, DateTimeKind.Local),
                RncContraparte = factura.Rnc,
                RazonSocialContraparte = factura.RazonSocial,
                Ncf = factura.Ncf,
                TipoBienServicio = tipoCompra[..2],
                TipoIngresoGasto = "Compra de bienes y servicios",
                MontoGravadoBienes = bienes.ToString("F2", CultureInfo.InvariantCulture),
                MontoGravadoServicios = servicios.ToString("F2", CultureInfo.InvariantCulture),
                MontoGravadoTotal = gravado.ToString("F2", CultureInfo.InvariantCulture),
                MontoExento = exento.ToString("F2", CultureInfo.InvariantCulture),
                TotalFacturado = total.ToString("F2", CultureInfo.InvariantCulture),
                ItbisFacturado = itbis.ToString("F2", CultureInfo.InvariantCulture),
                ItbisRetenido = "0.00",
                IsrRetenido = "0.00",
                OtrosImpuestos = "0.00",
                PropinaLegal = "0.00",
                CondicionPago = codigoPago,
                MetodoPago = codigoPago,
                FuenteJson = System.Text.Json.JsonSerializer.Serialize(new { Archivo = _archivo, ImagenEscaneada = _rutaFotoEscaneadaActual })
            };
            await _db.GuardarCompraCapturadaAsync(registro);
            ProgresoLabel.Text = "✅ FACTURA ESCANEADA, LEÍDA Y CARGADA CORRECTAMENTE PARA EL ARCHIVO 606 (COMPRAS)";
            ResultadoLoteLabel.Text = ProgresoLabel.Text;
            ResultadoLoteLabel.IsVisible = true;
            Compra606Seccion.IsVisible = false;
            _esperandoRevision = false;
            _capturaCompra606Actual = false;
            _loteActivo = false;
            OmitirButton.IsVisible = false;
            CargarButton.IsEnabled = true;
            CargarButton.Text = "Cargar facturas (PDF/Imagen)";
            CapturarCompraButton.IsEnabled = true;
            await ActualizarAsync();
        }
        catch (Exception ex)
        {
            ProgresoLabel.Text = $"No se pudo cargar la compra al 606: {ex.Message}";
            _logger.LogError(ex, "No se pudo importar la factura escaneada al libro fiscal 606.");
        }
        finally { ImportarCompra606Button.IsEnabled = true; }
    }

    private Task AgregarArchivosAsync(IEnumerable<FacturaPendiente> archivos)
    {
        var agregados = archivos.ToList();
        if (agregados.Count == 0) return Task.CompletedTask;
        if (_loteActivo) return DisplayAlertAsync("Procesamiento en curso", "Espera a que termine el lote actual antes de cargar más facturas.", "Aceptar");
        _loteActivo = true;
        _totalArchivos = 0;
        _numeroProcesado = 0;
        _duplicadasLote = 0;
        _pendientesLote = 0;
        _exitos.Clear();
        _duplicadosDetalle.Clear();
        _pendientesLoteIds.Clear();
        ResultadoLoteLabel.Text = string.Empty;
        ResultadoLoteLabel.IsVisible = false;
        foreach (var archivo in agregados) _cola.Enqueue(archivo);
        _totalArchivos += agregados.Count;
        ActualizarArchivosLabel();
        return ProcesarSiguienteAsync();
    }

    private void ActualizarArchivosLabel()
    {
        var nombres = (_archivo is null ? Enumerable.Empty<string>() : new[] { _archivo }).Concat(_cola.Select(archivo => archivo.Nombre));
        ArchivoLabel.Text = nombres.Any() ? "Archivos pendientes:\n" + string.Join("\n", nombres.Select(nombre => $"• {nombre}")) : "Sin archivos seleccionados. También puedes arrastrar archivos a esta página.";
    }

    private async Task ProcesarSiguienteAsync()
    {
        if (_procesandoCola || _esperandoRevision) return;
        _procesandoCola = true;
        CargarButton.IsEnabled = false;
        CargarButton.Text = "🔄 PROCESANDO FACTURAS… ESPERE";
        GuardarButton.IsEnabled = false;
        foreach (var campo in new[] { RncEntry, RazonEntry, NcfEntry, SubtotalEntry, ItbisEntry, TotalEntry }) campo.IsEnabled = false;
        MonedaPicker.IsEnabled = false;
        try
        {
            while (_cola.Count > 0)
            {
                LimpiarFormulario();
                var pendiente = _cola.Dequeue();
                _archivo = pendiente.Nombre;
                _rutaFotoEscaneadaActual = pendiente.RutaImagenPermanente;
                _numeroProcesado++;
                ActualizarArchivosLabel();
                ProgresoLoteLabel.Text = $"Procesando archivo {_numeroProcesado} de {_totalArchivos}: {_archivo}";
                ProgresoLabel.Text = string.Empty;
                LimpiarEstadoPlantilla();
                var rutaTemporal = string.Empty;
                try
                {
                    rutaTemporal = await CopiarATemporalAsync(pendiente);
                    _rutaFacturaTemporal = rutaTemporal;
                    VerFacturaButton.IsVisible = true;
                    _textoOcrActual = await _tesseract.LeerTextoDeImagenAsync(rutaTemporal);
                    var lectura = await ExtraerDatosDesdeTextoOCR(_textoOcrActual);
                    _facturaEnRevision = CrearFactura(lectura, pendiente.Nombre);
                    string? motivo = null;
                    CapturaFacturaDgii? factura = null;
                    try { factura = CrearFacturaValidada(); }
                    catch (InvalidOperationException ex) { motivo = ex.Message; }
                    motivo ??= CapturaFacturaValidacion.MotivoRevisionAutomatica(_datosCliente, _proveedorValidado, PlantillaNombreLabel.IsVisible && !_plantillaConfirmadaPorOcr);
                    if (pendiente.RequiereRevisionManual606 || motivo is not null)
                    {
                        _capturaCompra606Actual = pendiente.RequiereRevisionManual606;
                        Compra606Seccion.IsVisible = _capturaCompra606Actual;
                        MontoExentoCompraEntry.Text = "0.00";
                        GuardarButton.IsVisible = !_capturaCompra606Actual;
                        FechaCompraPicker.Date = DateTime.Today;
                        TipoCompraPicker.SelectedIndex = -1;
                        BaseCompraPicker.SelectedIndex = -1;
                        MetodoPagoCompraPicker.SelectedIndex = -1;
                        _esperandoRevision = true;
                        OmitirButton.IsVisible = true;
                        GuardarButton.IsEnabled = true;
                        ProgresoLabel.Text = pendiente.RequiereRevisionManual606
                            ? "⚠️ Revisa los datos OCR, confirma la fecha, clasificación, base gravada y método de pago, y luego carga la compra al libro fiscal 606."
                            : $"⚠️ FACTURA PENDIENTE DE VALIDACIÓN. COMPLETE LOS CAMPOS FALTANTES PARA GUARDAR. {motivo} Corrige o elige omitir para continuar.";
                        return;
                    }
                    if (await _capturas.ExisteNcfAsync(UsuarioId, factura!.Ncf))
                        throw new NcfDuplicadoException(factura.Ncf);
                    await _capturas.GuardarAsync(factura);
                    _exitos.Add(factura);
                    ResultadoLoteLabel.Text = $"✅ FACTURA COMPLETADA, VALIDADA Y GUARDADA AUTOMÁTICAMENTE — {pendiente.Nombre} (NCF: {factura.Ncf})";
                    ResultadoLoteLabel.IsVisible = true;
                    LimpiarFormulario();
                }
                catch (NcfDuplicadoException ex)
                {
                    _duplicadasLote++;
                    _duplicadosDetalle.Add($"{pendiente.Nombre}: NCF {ex.Ncf} ya guardado.");
                    ResultadoLoteLabel.Text = $"⚠️ FACTURA DUPLICADA (NCF: {ex.Ncf}), SE SALTA ESTA FACTURA";
                    ResultadoLoteLabel.IsVisible = true;
                    LimpiarFormulario();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "No se pudo procesar o guardar el archivo {Archivo}.", pendiente.Nombre);
                    _facturaEnRevision ??= new CapturaFacturaDgii { UsuarioId = UsuarioId, Archivo = pendiente.Nombre };
                    _capturaCompra606Actual = pendiente.RequiereRevisionManual606;
                    Compra606Seccion.IsVisible = _capturaCompra606Actual;
                    GuardarButton.IsVisible = !_capturaCompra606Actual;
                    MontoExentoCompraEntry.Text = "0.00";
                    FechaCompraPicker.Date = DateTime.Today;
                    TipoCompraPicker.SelectedIndex = -1;
                    BaseCompraPicker.SelectedIndex = -1;
                    MetodoPagoCompraPicker.SelectedIndex = -1;
                    _esperandoRevision = true;
                    OmitirButton.IsVisible = true;
                    GuardarButton.IsEnabled = true;
                    ProgresoLabel.Text = pendiente.RequiereRevisionManual606
                        ? $"⚠️ No se pudo leer la imagen automáticamente ({ex.Message}). Completa y verifica los campos manualmente antes de cargar al 606."
                        : $"⚠️ FACTURA PENDIENTE DE VALIDACIÓN. COMPLETE LOS CAMPOS FALTANTES PARA GUARDAR. {ex.Message} Corrige o elige omitir para continuar.";
                    return;
                }
                finally
                {
                    if (!_esperandoRevision) LimpiarArchivoTemporal();
                }
            }

            _archivo = null;
            ActualizarArchivosLabel();
            try { await ActualizarAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "El lote terminó, pero no se pudo refrescar la lista de capturas.");
                EstadoLabel.Text = "El lote terminó, pero no se pudo actualizar la lista de capturas.";
            }
            ProgresoLoteLabel.Text = string.Empty;
            ProgresoLabel.Text = "✅ PROCESAMIENTO FINALIZADO. Puede cargar un nuevo lote de facturas";
            _loteActivo = false;
            MostrarResumen();
        }
        finally
        {
            _procesandoCola = false;
            CargarButton.IsEnabled = !_loteActivo;
            CapturarCompraButton.IsEnabled = !_loteActivo && !CapturaInteligenteOverlay.IsVisible;
            if (!_loteActivo) CargarButton.Text = "Cargar facturas (PDF/Imagen)";
            foreach (var campo in new[] { RncEntry, RazonEntry, NcfEntry, SubtotalEntry, ItbisEntry, TotalEntry }) campo.IsEnabled = true;
            MonedaPicker.IsEnabled = true;
            GuardarButton.IsEnabled = true;
            if (!_esperandoRevision)
            {
                _totalArchivos = 0;
                _numeroProcesado = 0;
            }
        }
    }

    private async Task<LecturaCapturaFacturaDgii> ExtraerDatosDesdeTextoOCR(string textoLeido)
    {
        RncEntry.Text = string.Empty;
        RazonEntry.Text = string.Empty;
        NcfEntry.Text = string.Empty;
        SubtotalEntry.Text = string.Empty;
        ItbisEntry.Text = string.Empty;
        TotalEntry.Text = string.Empty;
        _textoOcrActual = textoLeido;
        TextoOcrEditor.Text = textoLeido;
        TextoOcrSeccion.IsVisible = true;

        var ajustes = await _db.ObtenerAjustesAsync(UsuarioId);
        var lectura = CapturaFacturaDgiiParser.Analizar(textoLeido, ajustes.RncCompania);
        var plantilla = CapturaFacturaPlantillaService.Buscar(textoLeido);
        _plantillaConfirmadaPorOcr = plantilla is not null && CapturaFacturaPlantillaService.DatosConfirmadosPorOcr(plantilla, lectura);
        if (plantilla is not null)
        {
            lectura = CapturaFacturaPlantillaService.Aplicar(plantilla);
            PlantillaNombreLabel.Text = $"Plantilla detectada: {plantilla.Nombre}";
            PlantillaMensajeLabel.Text = plantilla.Mensaje;
            PlantillaNombreLabel.IsVisible = true;
            PlantillaMensajeLabel.IsVisible = true;
            MonedaLabel.Text = plantilla.MonedaOrigen == "USD"
                ? $"Valores mostrados y guardados en DOP. Importes originales en USD convertidos a tasa {plantilla.TasaCambio.ToString("0.0#", CultureInfo.InvariantCulture)}."
                : "Moneda: DOP - Pesos | Tasa de cambio: 1.00 | Sin conversión";
        }
        _actualizandoDesdeListado = true;
        RncEntry.Text = lectura.Rnc;
        RazonEntry.Text = lectura.RazonSocial;
        _actualizandoDesdeListado = false;
        await SincronizarProveedorConPadronAsync();
        _rncProveedor = RncEntry.Text?.Trim() ?? string.Empty;
        _razonSocialProveedor = RazonEntry.Text?.Trim() ?? string.Empty;
        lectura = lectura with { Rnc = _rncProveedor, RazonSocial = _razonSocialProveedor };
        _ncfComprobante = lectura.Ncf;
        _subtotal = lectura.Subtotal;
        _itbis = lectura.Itbis;
        _total = lectura.Total;
        _datosCliente = lectura.DatosCliente;
        _mostrandoLecturaOcr = true;
        NcfEntry.Text = _ncfComprobante;
        SubtotalEntry.Text = _subtotal;
        ItbisEntry.Text = _itbis;
        TotalEntry.Text = _total;
        _monedaActual = lectura.Moneda;
        MonedaPicker.SelectedIndex = lectura.Moneda == "USD" ? 1 : lectura.Moneda == "DOP" ? 0 : -1;
        if (plantilla is null)
            MonedaLabel.Text = lectura.Moneda == "AMBIGUA"
                ? "Moneda ambigua: selecciona DOP o USD antes de guardar."
                : $"Moneda detectada: {lectura.Moneda}";

        ActualizarAdvertenciasCampos();
        ActualizarBotonRnc();
        return lectura;
    }

    private void ActualizarAdvertenciasCampos()
    {
        if (!_mostrandoLecturaOcr) return;
        var campos = new (string Nombre, string? Valor)[]
        {
            ("RNC del proveedor", RncEntry.Text), ("Razón social", RazonEntry.Text),
            ("NCF", NcfEntry.Text), ("Subtotal", SubtotalEntry.Text),
            ("ITBIS", ItbisEntry.Text), ("Total", TotalEntry.Text)
        };
        var advertencias = campos.Where(campo => string.IsNullOrWhiteSpace(campo.Valor))
            .Select(campo => $"No se pudo leer {campo.Nombre}, por favor complétalo manualmente").ToList();
        if (_datosCliente) advertencias.Add("El RNC y la razón social detectados corresponden al cliente, no al proveedor. Verifica antes de guardar.");
        if (MonedaPicker.SelectedIndex < 0) advertencias.Add("Moneda ambigua: selecciona DOP o USD.");
        AdvertenciasLabel.Text = string.Join(Environment.NewLine, advertencias.Select(mensaje => $"⚠️ {mensaje}"));
        AdvertenciasLabel.IsVisible = advertencias.Count > 0;
    }

    private void LimpiarEstadoPlantilla()
    {
        PlantillaNombreLabel.Text = string.Empty;
        PlantillaNombreLabel.IsVisible = false;
        PlantillaMensajeLabel.Text = string.Empty;
        PlantillaMensajeLabel.IsVisible = false;
    }

    private void LimpiarTextoOcrYAdvertencias()
    {
        LimpiarEstadoPlantilla();
        _textoOcrActual = string.Empty;
        _mostrandoLecturaOcr = false;
        _datosCliente = false;
        _plantillaConfirmadaPorOcr = false;
        _rncProveedor = _razonSocialProveedor = _ncfComprobante = _subtotal = _itbis = _total = string.Empty;
        ListaRncEstadoLabel.IsVisible = false;
        ActualizarBotonRnc();
        TextoOcrEditor.Text = string.Empty;
        TextoOcrSeccion.IsVisible = false;
        AdvertenciasLabel.Text = string.Empty;
        AdvertenciasLabel.IsVisible = false;
        ProgresoLoteLabel.Text = string.Empty;
    }

    private CapturaFacturaDgii CrearFactura(LecturaCapturaFacturaDgii lectura, string archivo) => new()
    {
        UsuarioId = UsuarioId,
        Rnc = lectura.Rnc,
        RazonSocial = lectura.RazonSocial,
        Ncf = lectura.Ncf,
        Subtotal = lectura.Subtotal,
        Itbis = lectura.Itbis,
        Total = lectura.Total,
        Moneda = lectura.Moneda,
        Archivo = archivo
    };

    private void AgregarRevision(CapturaFacturaDgii factura, string detalle, string textoOcr = "", bool datosCliente = false) =>
        _revisiones.Add(new CapturaFacturaRevision(factura, detalle, textoOcr, datosCliente));

    private void MostrarResumen()
    {
        ResultadoResumenLabel.Text = ResultadoLoteLabel.Text;
        ResultadoResumenLabel.IsVisible = ResultadoLoteLabel.IsVisible;
        ExitosLista.ItemsSource = null;
        ExitosLista.ItemsSource = _exitos.ToList();
        RevisionesLista.ItemsSource = null;
        RevisionesLista.ItemsSource = _revisiones.ToList();
        TotalExitosLabel.Text = $"✅ Facturas guardadas correctamente: {_exitos.Count}";
        PendientesLoteLabel.Text = $"⚠️ Facturas pendientes por validar: {_pendientesLote}";
        DuplicadasLoteLabel.Text = $"❌ Facturas duplicadas saltadas: {_duplicadasLote}";
        DuplicadasDetalleLabel.Text = string.Join(Environment.NewLine, _duplicadosDetalle);
        DuplicadasDetalleLabel.IsVisible = _duplicadosDetalle.Count > 0;
        TotalRevisionLabel.Text = $"Total: {_revisiones.Count} facturas requieren revisión manual";
        SeccionRevision.IsVisible = _revisiones.Count > 0;
        ResumenOverlay.IsVisible = true;
    }

    private void OnCerrarResumenClicked(object? sender, EventArgs e) => ResumenOverlay.IsVisible = false;

    private void OnRevisionSeleccionada(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not CapturaFacturaRevision revision) return;
        RevisionesLista.SelectedItem = null;
        _revisionActual = revision;
        _idEdicion = null;
        _facturaEnRevision = null;
        _esperandoRevision = false;
        CargarEnFormulario(revision.Factura);
        _textoOcrActual = revision.TextoOcr;
        _datosCliente = revision.DatosCliente;
        TextoOcrEditor.Text = revision.TextoOcr;
        TextoOcrSeccion.IsVisible = !string.IsNullOrWhiteSpace(revision.TextoOcr);
        AdvertenciasLabel.Text = revision.Detalle;
        AdvertenciasLabel.IsVisible = true;
        OmitirButton.IsVisible = false;
        ProgresoLabel.Text = $"Revisión manual: {revision.Detalle}";
        ResumenOverlay.IsVisible = false;
        GuardarButton.Text = "Guardar factura corregida";
    }

    private void OnExitoSeleccionado(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not CapturaFacturaDgii factura) return;
        ExitosLista.SelectedItem = null;
        _revisionActual = null;
        _idEdicion = factura.Id;
        CargarEnFormulario(factura);
        ProgresoLabel.Text = "Factura guardada. Puedes revisar o editar los datos y guardar los cambios.";
        GuardarButton.Text = "Actualizar factura";
        ResumenOverlay.IsVisible = false;
    }

    private void CargarEnFormulario(CapturaFacturaDgii factura)
    {
        RncEntry.Text = factura.Rnc;
        RazonEntry.Text = factura.RazonSocial;
        NcfEntry.Text = factura.Ncf;
        SubtotalEntry.Text = factura.Subtotal;
        ItbisEntry.Text = factura.Itbis;
        TotalEntry.Text = factura.Total;
        _archivo = factura.Archivo;
        _monedaActual = factura.Moneda;
        MonedaPicker.SelectedIndex = factura.Moneda == "USD" ? 1 : factura.Moneda == "DOP" ? 0 : -1;
        MonedaLabel.Text = factura.Moneda == "AMBIGUA" ? "Moneda ambigua: selecciona DOP o USD antes de guardar." : $"Moneda: {_monedaActual}. Verifica los datos antes de guardar.";
        ArchivoLabel.Text = $"Archivo: {_archivo}";
    }

    private async Task<string> CopiarATemporalAsync(FacturaPendiente pendiente)
    {
        const long maximoBytes = 50 * 1024 * 1024;
        Directory.CreateDirectory(FileSystem.CacheDirectory);
        var destino = Path.Combine(FileSystem.CacheDirectory, $"captura-{Guid.NewGuid():N}{Path.GetExtension(pendiente.Nombre)}");
        try
        {
            await using var origen = await pendiente.AbrirAsync();
            await using var salida = new FileStream(destino, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            int leidos;
            while ((leidos = await origen.ReadAsync(buffer)) > 0)
            {
                total += leidos;
                if (total > maximoBytes) throw new InvalidDataException("El archivo supera el límite de 50 MB.");
                await salida.WriteAsync(buffer.AsMemory(0, leidos));
            }
            if (total == 0) throw new InvalidDataException("El archivo está vacío.");
            return destino;
        }
        catch
        {
            try
            {
                if (File.Exists(destino)) File.Delete(destino);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo eliminar la copia temporal incompleta {Archivo}.", destino);
            }
            throw;
        }
    }



    private async void OnVerFacturaClicked(object? sender, EventArgs e)
    {
        try
        {
            var ruta = _rutaFacturaTemporal;
            if (string.IsNullOrEmpty(ruta) || !File.Exists(ruta)) throw new FileNotFoundException("La factura actual ya no está disponible para abrirla.");
            if (!await Launcher.Default.OpenAsync(new OpenFileRequest("Factura actual", new ReadOnlyFile(ruta))))
                throw new InvalidOperationException("No se encontró una aplicación para abrir la factura.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la factura actual.");
            await DisplayAlertAsync("Ver factura", ex.Message, "Aceptar");
        }
    }

    private void LimpiarArchivoTemporal()
    {
        var ruta = _rutaFacturaTemporal;
        _rutaFacturaTemporal = null;
        VerFacturaButton.IsVisible = false;
        if (string.IsNullOrEmpty(ruta)) return;
        try { File.Delete(ruta); }
        catch (Exception ex) { _logger.LogWarning(ex, "No se pudo eliminar el archivo temporal de OCR {Archivo}.", ruta); }
    }

    private void LimpiarFormulario()
    {
        LimpiarArchivoTemporal();
        RncEntry.Text = RazonEntry.Text = NcfEntry.Text = SubtotalEntry.Text = ItbisEntry.Text = TotalEntry.Text = string.Empty;
        _archivo = null;
        _rutaFotoEscaneadaActual = null;
        _monedaActual = "DOP";
        _idEdicion = null;
        _revisionActual = null;
        _facturaEnRevision = null;
        _esperandoRevision = false;
        _capturaCompra606Actual = false;
        Compra606Seccion.IsVisible = false;
        GuardarButton.IsVisible = true;
        OmitirButton.IsVisible = false;
        LimpiarTextoOcrYAdvertencias();
        MonedaPicker.SelectedIndex = 0;
        MonedaLabel.Text = "Moneda: DOP - Pesos | Tasa de cambio: 1.00";
        GuardarButton.Text = "Guardar factura";
        ActualizarArchivosLabel();
    }

    private void OnMonedaChanged(object? sender, EventArgs e)
    {
        if (MonedaPicker.SelectedIndex is 0 or 1)
        {
            _monedaActual = MonedaPicker.SelectedIndex == 1 ? "USD" : "DOP";
            MonedaLabel.Text = $"Moneda seleccionada: {_monedaActual}. Verifica antes de guardar.";
        }
        ActualizarAdvertenciasCampos();
    }

    private CapturaFacturaDgii CrearFacturaValidada()
    {
        var factura = new CapturaFacturaDgii
        {
            Id = _idEdicion ?? _facturaEnRevision?.Id ?? Guid.NewGuid().ToString("N"),
            UsuarioId = UsuarioId, Rnc = RncEntry.Text ?? string.Empty, RazonSocial = RazonEntry.Text ?? string.Empty,
            Ncf = NcfEntry.Text ?? string.Empty, Subtotal = SubtotalEntry.Text ?? string.Empty,
            Itbis = ItbisEntry.Text ?? string.Empty, Total = TotalEntry.Text ?? string.Empty,
            Moneda = MonedaPicker.SelectedIndex == 1 ? "USD" : MonedaPicker.SelectedIndex == 0 ? "DOP" : string.Empty,
            Archivo = _archivo ?? string.Empty
        };
        CapturaFacturaValidacion.Validar(factura);
        return factura;
    }

    private async void OnGuardarClicked(object? sender, EventArgs e)
    {
        GuardarButton.IsEnabled = false;
        var regresarAlResumen = _revisionActual is not null || _idEdicion is not null;
        var continuarLote = _esperandoRevision;
        try
        {
            var factura = CrearFacturaValidada();
            if (_datosCliente && !await DisplayAlertAsync("Verificar contraparte", "Los datos detectados proceden de la sección Cliente, no del proveedor. Verifica los campos antes de guardarlos en esta captura.", "Guardar", "Revisar")) return;
            if (_idEdicion is null) await _capturas.GuardarAsync(factura);
            else await _capturas.ActualizarAsync(factura);

            if (_revisionActual is not null)
            {
                _revisiones.Remove(_revisionActual);
                if (_pendientesLoteIds.Remove(_revisionActual.Factura.Id)) _pendientesLote--;
            }
            _exitos.RemoveAll(item => item.Id == factura.Id);
            _exitos.Add(factura);
            LimpiarFormulario();
            try { await ActualizarAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "La captura se guardó, pero no se pudo actualizar la lista.");
                await DisplayAlertAsync("Captura DGII", "La factura se guardó, pero la lista no pudo actualizarse. Vuelve a abrir la pantalla para verificarla.", "Aceptar");
            }
            if (continuarLote) await ProcesarSiguienteAsync();
            else if (regresarAlResumen) MostrarResumen();
            else ProgresoLabel.Text = "Factura guardada correctamente.";
        }
        catch (NcfDuplicadoException ex)
        {
            var mensaje = continuarLote
                ? $"⚠️ FACTURA DUPLICADA (NCF: {ex.Ncf}), SE SALTA ESTA FACTURA"
                : $"⚠️ ALERTA: Esta factura (NCF: {ex.Ncf}) YA ESTÁ GUARDADA en el sistema.";
            await DisplayAlertAsync("Factura duplicada", mensaje, "Aceptar");
            if (continuarLote)
            {
                _duplicadasLote++;
                _duplicadosDetalle.Add($"{_archivo}: NCF {ex.Ncf} ya guardado.");
                ResultadoLoteLabel.Text = mensaje;
                ResultadoLoteLabel.IsVisible = true;
                LimpiarFormulario();
                await ProcesarSiguienteAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la captura DGII.");
            await DisplayAlertAsync("Captura DGII", ex.Message, "Aceptar");
        }
        finally { GuardarButton.IsEnabled = true; }
    }

    private async void OnOmitirClicked(object? sender, EventArgs e)
    {
        if (!_esperandoRevision || _facturaEnRevision is null) return;
        _facturaEnRevision.Rnc = RncEntry.Text ?? string.Empty;
        _facturaEnRevision.RazonSocial = RazonEntry.Text ?? string.Empty;
        _facturaEnRevision.Ncf = NcfEntry.Text ?? string.Empty;
        _facturaEnRevision.Subtotal = SubtotalEntry.Text ?? string.Empty;
        _facturaEnRevision.Itbis = ItbisEntry.Text ?? string.Empty;
        _facturaEnRevision.Total = TotalEntry.Text ?? string.Empty;
        _facturaEnRevision.Moneda = MonedaPicker.SelectedIndex == 1 ? "USD" : MonedaPicker.SelectedIndex == 0 ? "DOP" : "AMBIGUA";
        if (_pendientesLoteIds.Add(_facturaEnRevision.Id)) _pendientesLote++;
        var detalle = AdvertenciasLabel.Text is { Length: > 0 } advertencias ? advertencias : ProgresoLabel.Text;
        AgregarRevision(_facturaEnRevision, $"Omitida por el usuario; verificar manualmente. {detalle}", _textoOcrActual, _datosCliente);
        LimpiarFormulario();
        await ProcesarSiguienteAsync();
    }

    protected override void OnDisappearing()
    {
        _capturaCamaraCancellation?.Cancel();
        CapturaInteligenteOverlay.IsVisible = false;
#if WINDOWS
        DesactivarArrastreArchivos();
#endif
        base.OnDisappearing();
    }

    private async void OnConfiguracionApiDgiiClicked(object? sender, EventArgs e)
    {
        await DisplayAlertAsync("Configuración reservada para API DGII",
            "La ruta ya requiere autorización de administrador, pero esta configuración permanece bloqueada hasta integrar la API oficial y contar con autorización escrita de la DGII. No se solicitan ni guardan los códigos de la tarjeta.", "Aceptar");
    }

    private async void OnEnvio606Clicked(object? sender, EventArgs e)
    {
        try
        {
            var pagina = _services.GetRequiredService<Envio606Page>();
            Shell.SetNavBarIsVisible(pagina, true);
            await Navigation.PushAsync(pagina);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la preparación del Formato 606.");
            await DisplayAlertAsync("Formato 606", "No se pudo abrir la pantalla de preparación.", "Aceptar");
        }
    }

    private async void OnReportesClicked(object? sender, EventArgs e)
    {
        try
        {
            var pagina = _services.GetRequiredService<PaginaDGII>();
            Shell.SetNavBarIsVisible(pagina, true);
            await Navigation.PushAsync(pagina);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la página de reportes DGII.");
            await DisplayAlertAsync("Reportes DGII", "No se pudo abrir la página de reportes.", "Aceptar");
        }
    }

    private async void OnCambiarContrasenaClicked(object? sender, EventArgs e)
    {
        try
        {
            var correo = _auth.CurrentUser?.Correo ?? throw new InvalidOperationException("Inicia sesión para cambiar la contraseña.");
            if (!await DisplayAlertAsync("Cambiar contraseña", $"Se enviará un enlace de restablecimiento a {correo}.", "Enviar", "Cancelar")) return;
            await _auth.SendResetAsync(correo);
            await DisplayAlertAsync("Cambiar contraseña", "Se solicitó el envío de instrucciones al correo de la cuenta.", "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo solicitar el cambio de contraseña.");
            await DisplayAlertAsync("Cambiar contraseña", AuthService.UserMessage(ex), "Aceptar");
        }
    }
}
