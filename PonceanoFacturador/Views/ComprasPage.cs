using System.Globalization;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public sealed class ComprasPage : ContentPage
{
    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly LecturaDocumentoFiscalService _lector;
    private readonly BorradorFiscal606607OllamaService _borradorFiscal;
    private readonly RegistroLecturaOcrService _registroOcr;
    private readonly ILogger<ComprasPage> _logger;
    private readonly Label _estado = new() { TextColor = Color.FromArgb("#60788F") };
    private readonly VerticalStackLayout _bajoMinimo = new() { Spacing = 8 };
    private readonly VerticalStackLayout _pedidos = new() { Spacing = 12 };

    public ComprasPage(DatabaseService db, AuthService auth, LecturaDocumentoFiscalService lector, BorradorFiscal606607OllamaService borradorFiscal, RegistroLecturaOcrService registroOcr, ILogger<ComprasPage> logger)
    {
        _db = db;
        _auth = auth;
        _lector = lector;
        _borradorFiscal = borradorFiscal;
        _registroOcr = registroOcr;
        _logger = logger;
        Title = "Compras e inventario";
        var sugerir = new Button { Text = "Crear pedido desde sugerencias", BackgroundColor = Color.FromArgb("#467FA6"), TextColor = Colors.White };
        sugerir.Clicked += OnCrearPedidoClicked;
        var actualizar = new Button { Text = "Actualizar", BackgroundColor = Color.FromArgb("#F1F1F1"), TextColor = Color.FromArgb("#555555") };
        actualizar.Clicked += async (_, _) => await CargarAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24, Spacing = 14, MaximumWidthRequest = 900,
                Children =
                {
                    new Label { Text = "Inventario, compras y sugerencias", FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#667D91") },
                    new Label { Text = "Los artículos se descuentan al emitir ventas cuando la línea está vinculada al catálogo. La recepción de un pedido actualiza el stock y el libro interno de compras en una sola transacción." },
                    new Label { Text = "Productos bajo el mínimo", FontSize = 18, FontAttributes = FontAttributes.Bold },
                    _bajoMinimo, sugerir,
                    new Label { Text = "Pedidos pendientes", FontSize = 18, FontAttributes = FontAttributes.Bold }, _pedidos,
                    actualizar, _estado
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await CargarAsync();
    }

    private string UsuarioId => _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para administrar compras.");

    private async Task CargarAsync()
    {
        try
        {
            var sugeridos = await _db.GetArticulosBajoMinimoAsync();
            _bajoMinimo.Children.Clear();
            if (sugeridos.Count == 0) _bajoMinimo.Children.Add(new Label { Text = "No hay artículos por debajo de su mínimo." });
            foreach (var articulo in sugeridos)
                _bajoMinimo.Children.Add(new Label { Text = $"{articulo.Descripcion} · stock {articulo.Stock} / mínimo {articulo.StockMinimo} · último costo RD$ {articulo.UltimoCosto:N2}" });

            _pedidos.Children.Clear();
            var pendientes = await _db.ObtenerPedidosCompraPendientesAsync(UsuarioId);
            if (pendientes.Count == 0) _pedidos.Children.Add(new Label { Text = "No hay pedidos pendientes." });
            foreach (var pedido in pendientes)
            {
                var detalles = await _db.ObtenerDetallesPedidoCompraAsync(pedido.Id);
                var caja = new Border { Stroke = Color.FromArgb("#DFE4E8"), Padding = 12, Content = new VerticalStackLayout { Spacing = 6 } };
                var contenido = (VerticalStackLayout)caja.Content;
                contenido.Children.Add(new Label { Text = $"Pedido {pedido.Fecha.ToLocalTime():dd/MM/yyyy HH:mm} · {detalles.Count} artículo(s)", FontAttributes = FontAttributes.Bold });
                foreach (var detalle in detalles) contenido.Children.Add(new Label { Text = $"{detalle.DescripcionArticulo}: {detalle.CantidadPedida} unidad(es)" });
                var cargarOcr = new Button { Text = "📷 Cargar factura (OCR)", BackgroundColor = Color.FromArgb("#467FA6"), TextColor = Colors.White };
                cargarOcr.Clicked += async (_, _) => await OnCargarFacturaOcrClicked(pedido);
                var recibir = new Button { Text = "Registrar factura y recibir mercancía", BackgroundColor = Color.FromArgb("#55B85B"), TextColor = Colors.White };
                recibir.Clicked += async (_, _) => await OnRecibirClicked(pedido);
                contenido.Children.Add(cargarOcr);
                contenido.Children.Add(recibir);
                _pedidos.Children.Add(caja);
            }
            _estado.Text = "Inventario y pedidos actualizados.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo cargar el inventario y los pedidos.");
            _estado.Text = "No se pudo cargar la información de compras.";
        }
    }

    private async void OnCrearPedidoClicked(object? sender, EventArgs e)
    {
        try
        {
            var articulos = await _db.GetArticulosBajoMinimoAsync();
            if (articulos.Count == 0) { await DisplayAlertAsync("Compras", "No hay productos bajo mínimo para sugerir.", "Aceptar"); return; }
            var detalles = new List<DetallePedidoCompra>();
            foreach (var articulo in articulos)
            {
                var sugerencia = Math.Max(1, articulo.StockMinimo - articulo.Stock);
                var texto = await DisplayPromptAsync("Cantidad a pedir", $"{articulo.Descripcion} (stock: {articulo.Stock}, mínimo: {articulo.StockMinimo})", initialValue: sugerencia.ToString(CultureInfo.InvariantCulture), keyboard: Keyboard.Numeric);
                if (texto is null) return;
                if (!int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cantidad) || cantidad <= 0)
                { await DisplayAlertAsync("Compras", $"La cantidad para {articulo.Descripcion} no es válida.", "Aceptar"); return; }
                detalles.Add(new DetallePedidoCompra { ArticuloId = articulo.Id, DescripcionArticulo = articulo.Descripcion, CantidadPedida = cantidad });
            }
            await _db.CrearPedidoCompraAsync(UsuarioId, detalles);
            await CargarAsync();
            await DisplayAlertAsync("Compras", "Pedido pendiente creado. Ajusta cantidades y costos al registrar la recepción.", "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo crear el pedido de compra.");
            await DisplayAlertAsync("Compras", ex.Message, "Aceptar");
        }
    }

    private async Task OnCargarFacturaOcrClicked(PedidoCompra pedido)
    {
        FileResult? archivo = null;
        RegistroLecturaOcr? registro = null;
        string texto = string.Empty;
        string promptEnviado = "No se envió ninguna solicitud a Ollama.";
        string respuestaOllama = "Ollama no devolvió respuesta.";
        try
        {
            archivo = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Selecciona una factura de proveedor (PDF, JPG o PNG)",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.WinUI] = new[] { ".pdf", ".jpg", ".jpeg", ".png" }
                })
            });
            if (archivo is null) return;
            _estado.Text = $"📂 Archivo cargado: {archivo.FileName}";
            var extension = Path.GetExtension(archivo.FileName);
            if (!new[] { ".pdf", ".jpg", ".jpeg", ".png" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Solo se admiten facturas PDF, JPG o PNG.");
            if (string.IsNullOrWhiteSpace(archivo.FullPath) || !File.Exists(archivo.FullPath))
                throw new FileNotFoundException("No se pudo acceder al archivo seleccionado.", archivo.FullPath);

            texto = await _lector.ExtraerTextoAsync(archivo.FullPath);
            _estado.Text = "🤖 Enviado a Ollama... esperando respuesta...";
            var lecturaFija = await _borradorFiscal.ExtraerFacturaPonceanoAsync(texto);
            promptEnviado = lecturaFija.PromptEnviado;
            respuestaOllama = lecturaFija.RespuestaRecibida;
            var datosRegistro = lecturaFija.Datos;
            var observacionesRegistro = string.IsNullOrWhiteSpace(lecturaFija.Observaciones)
                ? "Ollama no proporcionó observaciones."
                : lecturaFija.Observaciones;
            var ajustes = await _db.ObtenerAjustesAsync(UsuarioId);
            var lecturaLocal = FacturaOcrParser.TryParse(texto, ajustes.RncCompania, out var documento, out var error, archivo.FileName);
            var esCompra = documento is not null && string.Equals(documento.TipoOperacion, "Compra", StringComparison.OrdinalIgnoreCase);
            var requiereConversion = documento?.MonedaOriginal == "USD";
            var requiereRevision = !lecturaLocal || documento is null || !esCompra || requiereConversion || !RegistroLecturaOcrService.LecturaCompleta(datosRegistro);
            if (!lecturaLocal || documento is null) observacionesRegistro += Environment.NewLine + $"Lectura fiscal local incompleta: {error}";
            else if (!esCompra) observacionesRegistro += Environment.NewLine + "El documento no se identificó como compra de proveedor.";
            else if (requiereConversion) observacionesRegistro += Environment.NewLine + "Factura en USD: requiere conversión a DOP antes de registrar la compra.";
            registro = await _registroOcr.CrearAsync(archivo.FullPath, texto, datosRegistro, false,
                observacionesRegistro, textoEnviadoOllama: promptEnviado, respuestaOllama: respuestaOllama, requiereRevision: requiereRevision);
            var mensajeArchivo = $"✅ Registro guardado en: {registro.RutaRegistro}\n📁 Factura movida a: {Path.GetDirectoryName(registro.RutaDocumento)}";
            _estado.Text = mensajeArchivo;
            await DisplayAlertAsync("Lectura OCR", mensajeArchivo, "Aceptar");
            if (!lecturaLocal || documento is null)
            {
                await DisplayAlertAsync("Lectura OCR", $"La lectura requiere revisión: {error}", "Aceptar");
                return;
            }
            if (!esCompra)
            {
                await DisplayAlertAsync("Lectura OCR", "El documento no parece una factura de compra; no se modificó el pedido.", "Aceptar");
                return;
            }
            if (requiereConversion)
            {
                await DisplayAlertAsync("Lectura OCR", "La factura está en dólares. Este formulario no convierte moneda: revisa la tasa y registra los importes en pesos manualmente. El pedido no se modificó.", "Aceptar");
                return;
            }

            var datosCompra = CrearDatosLectura(documento, null, ajustes.RncCompania, texto, archivo.FileName);
            await OnRecibirClicked(pedido, new CapturaOcrCompra(registro, documento, null, datosCompra, texto, datosRegistro));
        }
        catch (Exception ex)
        {
            if (ex is ErrorLecturaFacturaOllamaException errorOllama)
            {
                promptEnviado = errorOllama.PromptEnviado;
                respuestaOllama = errorOllama.RespuestaRecibida;
            }
            _logger.LogError(ex, "No se pudo procesar la factura {Archivo} mediante OCR.", archivo?.FileName ?? "(selector de archivos)");
            if (ex is RegistroOcrAlmacenamientoException)
            {
                _estado.Text = $"❌ {ex.Message}";
                await DisplayAlertAsync("Lectura OCR", ex.Message, "Aceptar");
                return;
            }
            if (registro is null)
            {
                try
                {
                    registro = await _registroOcr.CrearAsync(archivo?.FullPath, texto, null, true,
                        $"❌ Error de procesamiento: {ex.Message}", textoEnviadoOllama: promptEnviado,
                        respuestaOllama: respuestaOllama, nombreOriginal: archivo?.FileName ?? "Archivo no seleccionado");
                }
                catch (Exception registroEx)
                {
                    _logger.LogError(registroEx, "No se pudo guardar el registro de error OCR de {Archivo}.", archivo?.FileName ?? "(sin archivo)");
                    await DisplayAlertAsync("Lectura OCR", $"❌ Error: {ex.Message}. También falló el registro: {registroEx.Message}", "Aceptar");
                    _estado.Text = $"❌ Error: {ex.Message} — no se pudo guardar el registro.";
                    return;
                }
            }
            var destinoFactura = string.IsNullOrWhiteSpace(registro.RutaDocumento)
                ? "No se dispuso de una factura para mover."
                : $"📁 Factura movida a: {Path.GetDirectoryName(registro.RutaDocumento)}";
            await DisplayAlertAsync("Lectura OCR", $"❌ Error: {ex.Message}\n✅ Registro guardado en: {registro.RutaRegistro}\n{destinoFactura}", "Aceptar");
            _estado.Text = $"❌ Error: {ex.Message} — Registro: {registro.RutaRegistro}. {destinoFactura}";
        }
    }

    private async Task OnRecibirClicked(PedidoCompra pedido, CapturaOcrCompra? captura = null)
    {
        try
        {
            var detalles = await _db.ObtenerDetallesPedidoCompraAsync(pedido.Id);
            foreach (var detalle in detalles)
            {
                var cantidadTexto = await DisplayPromptAsync("Cantidad recibida", detalle.DescripcionArticulo, initialValue: detalle.CantidadPedida.ToString(CultureInfo.InvariantCulture), keyboard: Keyboard.Numeric);
                if (cantidadTexto is null) return;
                if (!int.TryParse(cantidadTexto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cantidad) || cantidad <= 0)
                    throw new InvalidOperationException($"La cantidad recibida de {detalle.DescripcionArticulo} debe ser mayor que cero.");
                var costoTexto = await DisplayPromptAsync("Costo unitario", detalle.DescripcionArticulo, initialValue: detalle.CostoUnitario, keyboard: Keyboard.Numeric);
                if (costoTexto is null) return;
                if (!decimal.TryParse(costoTexto, NumberStyles.Number, CultureInfo.CurrentCulture, out var costo) || costo < 0)
                    throw new InvalidOperationException($"El costo de {detalle.DescripcionArticulo} no es válido.");
                detalle.CantidadPedida = cantidad;
                detalle.CostoUnitario = costo.ToString("F2", CultureInfo.InvariantCulture);
                await _db.ActualizarDetallePedidoCompraAsync(detalle, UsuarioId);
            }

            var proveedorInicial = captura?.Datos.NombreEmisor ?? pedido.ProveedorNombre;
            var proveedor = await DisplayPromptAsync("Proveedor", "Nombre del proveedor:", initialValue: proveedorInicial);
            if (proveedor is null) return;
            var rncInicial = captura?.Datos.RncEmisor ?? pedido.ProveedorRnc;
            var rncTexto = await DisplayPromptAsync("Proveedor", "RNC/cédula (9 u 11 dígitos):", initialValue: rncInicial, keyboard: Keyboard.Numeric);
            if (rncTexto is null) return;
            var rnc = new string(rncTexto.Where(char.IsDigit).ToArray());
            var ncfInicial = captura?.Datos.Ncf ?? pedido.NcfProveedor;
            var ncf = await DisplayPromptAsync("Factura del proveedor", "NCF:", initialValue: ncfInicial);
            if (ncf is null) return;
            var fecha = DateTime.Today;
            if (captura is not null)
            {
                var fechaTexto = await DisplayPromptAsync("Factura del proveedor", "Fecha de emisión (dd/MM/aaaa):", initialValue: captura.Datos.FechaEmision?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
                if (fechaTexto is null) return;
                if (!DateTime.TryParseExact(fechaTexto, new[] { "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha))
                    throw new InvalidOperationException("La fecha de emisión no tiene un formato válido.");
            }
            var formaPagoOcr = captura is null ? null : captura.Borrador?.FormaPago ?? FacturaOcrParser.LeerFormaPagoExplicita(captura.Texto);
            string? formaPago = null;
            if (formaPagoOcr is not null && await DisplayAlertAsync("Forma/condición de pago", $"Se detectó: {formaPagoOcr}. ¿Deseas usarla?", "Usar", "Cambiar"))
                formaPago = formaPagoOcr;
            if (formaPago is null)
                formaPago = await DisplayActionSheetAsync("Forma/condición de pago", "Cancelar", null, "Efectivo", "Transferencia", "Tarjeta", "Crédito", "Cheque", "Otro");
            if (formaPago is null or "Cancelar") return;
            var gravadoInicial = captura?.Datos.Subtotal ?? 0;
            var exentoInicial = captura?.Borrador?.MontoExento ?? 0;
            if (captura?.Borrador?.MontoGravadoBienes is decimal bienes && captura.Borrador.MontoGravadoServicios is decimal servicios &&
                captura.Borrador.MontoExento is decimal exentoModelo && bienes + servicios + exentoModelo + captura.Documento.Itbis <= captura.Documento.MontoOriginal)
            {
                gravadoInicial = bienes + servicios;
                exentoInicial = exentoModelo;
            }
            var otrosInicial = captura is null ? 0 : Math.Max(0, captura.Documento.MontoOriginal - gravadoInicial - exentoInicial - captura.Documento.Itbis);
            var gravado = await PedirMontoAsync("Monto gravado (sin ITBIS)", captura is null ? null : gravadoInicial);
            if (gravado is null) return;
            var exento = await PedirMontoAsync("Monto exento", captura is null ? null : exentoInicial);
            if (exento is null) return;
            var itbis = await PedirMontoAsync("ITBIS facturado", captura is null ? null : captura.Datos.Itbis);
            if (itbis is null) return;
            var otros = await PedirMontoAsync("Otros impuestos", captura is null ? null : otrosInicial);
            if (otros is null) return;
            var total = await PedirMontoAsync("Total de la factura", captura is null ? null : captura.Datos.Total);
            if (total is null) return;

            await _db.RecibirPedidoCompraAsync(pedido.Id, UsuarioId, proveedor, rnc, ncf.Trim(), fecha,
                formaPago, gravado.Value, exento.Value, itbis.Value, otros.Value, total.Value);
            await CargarAsync();
            var mensaje = "Factura registrada, inventario actualizado y compra añadida al libro fiscal interno.";
            if (captura is not null)
            {
                var datosRevisados = CrearDatosRevisados(captura.DatosRegistro, rnc, proveedor, ncf.Trim(), fecha,
                    gravado.Value + exento.Value, itbis.Value, total.Value);
                try
                {
                    await _registroOcr.ActualizarAsync(captura.Registro, datosRevisados, true, "✅ Lectura revisada y factura registrada en compras.");
                    mensaje += $"\nRegistro: {captura.Registro.RutaRegistro}\nFactura: {captura.Registro.RutaDocumento}";
                    if (!RegistroLecturaOcrService.LecturaCompleta(datosRevisados))
                        mensaje += "\nLa lectura permanece en PorProcesar porque faltan datos.";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "La compra se registró pero no se pudo finalizar el registro OCR {RutaRegistro}.", captura.Registro.RutaRegistro);
                    mensaje += " La factura quedó registrada, pero el archivo OCR permanece pendiente por un error al actualizar el registro.";
                }
            }
            await DisplayAlertAsync("Compras", mensaje, "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo recibir el pedido de compra.");
            await DisplayAlertAsync("Compras", ex.Message, "Aceptar");
        }
    }

    private async Task<decimal?> PedirMontoAsync(string titulo, decimal? valorInicial = null)
    {
        var inicial = valorInicial?.ToString("F2", CultureInfo.InvariantCulture) ?? "0.00";
        var texto = await DisplayPromptAsync("Factura del proveedor", $"{titulo} (sin símbolo ni separador de miles):", initialValue: inicial, keyboard: Keyboard.Numeric);
        if (texto is null) return null;
        if (!decimal.TryParse(texto, NumberStyles.Number, CultureInfo.CurrentCulture, out var monto) || monto < 0)
            throw new InvalidOperationException($"El importe de {titulo} no es válido.");
        return monto;
    }

    private static DatosLecturaOcr CrearDatosLectura(DocumentoFiscalOcr documento, BorradorFiscal606607? borrador, string rncCompania, string texto, string nombreArchivo)
    {
        var subtotal = documento.Subtotal;
        if (borrador?.MontoGravadoBienes is decimal bienes && borrador.MontoGravadoServicios is decimal servicios && borrador.MontoExento is decimal exento)
            subtotal = bienes + servicios + exento;
        DateTime? fecha = DateTime.TryParseExact(borrador?.FechaComprobante, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fechaOllama)
            ? fechaOllama
            : documento.Fecha;
        var patronNcf = @"(?<![A-Z0-9])(?:[AB][0-9]{2}[ \t\r\n]*[0-9]{8}|E[0-9]{2}[ \t\r\n]*[0-9]{10}|[ACDF-Z][0-9]{2}[ \t\r\n]*[0-9]{17}|[A-Z0-9]{3}-[0-9]{3,4}-[0-9]{7,10})(?![A-Z0-9])";
        var ncfEnTexto = System.Text.RegularExpressions.Regex.IsMatch(texto, patronNcf, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var ncfEnNombre = System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(nombreArchivo), patronNcf, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var tieneSubtotalEtiquetado = System.Text.RegularExpressions.Regex.IsMatch(texto, @"\b(?:SUB[\s-]*TOTAL|BASE\s+IMPONIBLE|MONTO\s+GRAVADO)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var metodos = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RNC Emisor"] = borrador?.RncCedula is not null
                ? "Estructurado por Ollama y contrastado con la lectura fiscal local."
                : documento.ClasificacionSupuesta
                    ? "Deducido al comparar los RNC identificados con el RNC de la compañía; clasificación supuesta e incierta."
                    : "Clasificado por el parser local usando etiquetas/roles de RNC y comparación con el RNC de la compañía.",
            ["Nombre Emisor"] = string.IsNullOrWhiteSpace(documento.NombreContraparte)
                ? "No se encontró una razón social etiquetada — dato pendiente de revisión manual."
                : "Primera coincidencia del parser local después de Razón Social, Nombre, Proveedor o Emisor.",
            ["NCF"] = borrador?.Ncf is not null
                ? "Estructurado por Ollama y contrastado con el patrón local de NCF."
                : ncfEnTexto ? "Identificado en el texto por patrón local de NCF."
                : ncfEnNombre ? "Identificado por patrón local en el nombre del archivo." : "No se encontró — dato pendiente de revisión manual.",
            ["Fecha Emisión"] = borrador?.FechaComprobante is not null
                ? "Estructurada por Ollama en formato AAAA-MM-DD y contrastada con la fecha del parser local."
                : "Primera fecha válida detectada por el parser local en formatos día/mes/año o AAAA-MM-DD.",
            ["RNC Comprador"] = "RNC de la compañía tomado de la configuración; no se extrajo del documento.",
            ["Subtotal"] = borrador?.MontoGravadoBienes is not null && borrador.MontoGravadoServicios is not null && borrador.MontoExento is not null
                ? "Calculado sumando los montos gravados de bienes, servicios y exento estructurados por Ollama."
                : tieneSubtotalEtiquetado
                    ? "Leído por el parser local junto a una etiqueta Subtotal, Base Imponible o Monto Gravado."
                    : "Calculado por el parser local como Total menos ITBIS, al no detectar subtotal etiquetado.",
            ["ITBIS"] = borrador?.ItbisFacturado is not null
                ? "Estructurado por Ollama y contrastado con el importe fiscal leído localmente."
                : "Leído por el parser local junto a una etiqueta ITBIS/IVA; no se calculó como diferencia.",
            ["Total"] = borrador?.TotalMontoFacturado is not null
                ? "Estructurado por Ollama y contrastado con el importe total leído localmente."
                : "Leído por el parser local junto a la etiqueta Total o Valor Total."
        };
        var explicaciones = new List<string>();
        if (documento.ClasificacionSupuesta)
            explicaciones.Add("La clasificación Compra/Venta se supuso porque los RNC no tenían etiquetas de rol.");
        if (!ncfEnTexto && ncfEnNombre)
            explicaciones.Add("El NCF no apareció en el texto OCR y se recuperó del nombre del archivo.");
        if (borrador is null)
            explicaciones.Add("Ollama no devolvió un borrador estructurado; se usaron los resultados del parser local.");
        return new DatosLecturaOcr(
            borrador?.RncCedula ?? documento.RncEmisor,
            documento.NombreContraparte,
            borrador?.Ncf ?? documento.Ncf,
            fecha,
            rncCompania,
            subtotal,
            borrador?.ItbisFacturado ?? documento.Itbis,
            borrador?.TotalMontoFacturado ?? documento.MontoOriginal,
            metodos,
            explicaciones);
    }

    private static DatosLecturaOcr CrearDatosRevisados(DatosLecturaOcr originales, string rnc, string proveedor, string ncf, DateTime fecha,
        decimal subtotal, decimal itbis, decimal total)
    {
        var metodos = new Dictionary<string, string>(originales.Metodos ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var cambios = new List<string>(originales.ExplicacionProceso ?? Array.Empty<string>());
        RegistrarEdicion("RNC Emisor", originales.RncEmisor, rnc);
        RegistrarEdicion("Nombre Emisor", originales.NombreEmisor, proveedor);
        RegistrarEdicion("NCF", originales.Ncf, ncf);
        RegistrarEdicion("Fecha Emisión", originales.FechaEmision?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        RegistrarEdicion("Subtotal", originales.Subtotal, subtotal);
        RegistrarEdicion("ITBIS", originales.Itbis, itbis);
        RegistrarEdicion("Total", originales.Total, total);
        return originales with { RncEmisor = rnc, NombreEmisor = proveedor, Ncf = ncf, FechaEmision = fecha, Subtotal = subtotal, Itbis = itbis, Total = total, Metodos = metodos, ExplicacionProceso = cambios };

        void RegistrarEdicion(string campo, object? valorAnterior, object valorNuevo)
        {
            if (string.Equals(Convert.ToString(valorAnterior, CultureInfo.InvariantCulture), Convert.ToString(valorNuevo, CultureInfo.InvariantCulture), StringComparison.Ordinal)) return;
            metodos[campo] = "Editado o confirmado por el usuario durante la revisión manual.";
            cambios.Add($"{campo} fue modificado durante la revisión humana.");
        }
    }

    private sealed record CapturaOcrCompra(
        RegistroLecturaOcr Registro,
        DocumentoFiscalOcr Documento,
        BorradorFiscal606607? Borrador,
        DatosLecturaOcr Datos,
        string Texto,
        DatosLecturaOcr DatosRegistro);
}
