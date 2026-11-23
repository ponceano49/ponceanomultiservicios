using System.Globalization;
using PonceanoFacturador.Models;
using SkiaSharp;

namespace PonceanoFacturador.Services;

public sealed class DocumentoPdfService
{
    private readonly DatabaseService _db;
    private static readonly CultureInfo MonedaCultura = CultureInfo.GetCultureInfo("es-DO");

    public DocumentoPdfService(DatabaseService db) => _db = db;

    public async Task<string> CrearFacturaAsync(Factura factura, string usuarioId)
    {
        var ajustes = await _db.ObtenerAjustesAsync(usuarioId);
        var cliente = await _db.GetClienteByIdAsync(factura.ClienteId);
        return await CrearAsync(ajustes, cliente, new DocumentoPdf(
            "FACTURA", factura.ClienteNombre, factura.ClienteRnc,
            factura.Fecha.ToLocalTime(), factura.VenceFecha?.ToLocalTime(),
            string.IsNullOrWhiteSpace(factura.NCF) ? "Borrador — NCF no asignado" : factura.NCF,
            factura.Items, factura.Subtotal, factura.Itbis, factura.RecargoMonto, factura.Total,
            factura.Nota, factura.InstruccionPago, factura.MetodoPago, factura.FirmaEmpresaRuta, factura.FirmaClienteRuta),
            $"factura-{factura.Id:N}.pdf");
    }

    public async Task<string> CrearCotizacionAsync(Cotizacion cotizacion, string usuarioId)
    {
        var ajustes = await _db.ObtenerAjustesAsync(usuarioId);
        var cliente = await _db.GetClienteByIdAsync(cotizacion.ClienteId);
        return await CrearAsync(ajustes, cliente, new DocumentoPdf(
            "COTIZACIÓN", cotizacion.ClienteNombre, cliente?.RncCedula ?? string.Empty,
            cotizacion.Fecha.ToLocalTime(), cotizacion.ValidezHasta, null,
            cotizacion.Items, cotizacion.Subtotal, cotizacion.Itbis, 0m, cotizacion.Total,
            cotizacion.Nota, string.Empty, string.Empty, cotizacion.FirmaEmpresaRuta, cotizacion.FirmaClienteRuta),
            $"cotizacion-{cotizacion.Id:N}.pdf");
    }

    private static async Task<string> CrearAsync(AjustesUsuario ajustes, Cliente? cliente, DocumentoPdf datos, string nombre)
    {
        if (string.IsNullOrWhiteSpace(ajustes.NombreCompania) || string.IsNullOrWhiteSpace(ajustes.RncCompania) ||
            string.IsNullOrWhiteSpace(ajustes.Telefonos) || string.IsNullOrWhiteSpace(ajustes.CorreoFactura))
            throw new InvalidOperationException("Completa el nombre, RNC, teléfonos y correo en Ajustes → Información de la Compañía antes de generar el PDF.");
        if (string.IsNullOrWhiteSpace(ajustes.LogoRuta) || !File.Exists(ajustes.LogoRuta))
            throw new InvalidOperationException("Carga el logo en Ajustes → Información de la Compañía antes de generar el PDF.");

        using var logo = SKBitmap.Decode(ajustes.LogoRuta) ?? throw new InvalidOperationException("No se pudo leer el logo de la compañía. Selecciona una imagen válida.");
        using var fuenteStream = await FileSystem.OpenAppPackageFileAsync("pdf-opensans.ttf");
        using var negritaStream = await FileSystem.OpenAppPackageFileAsync("pdf-opensans-semibold.ttf");
        using var fuente = SKTypeface.FromStream(fuenteStream) ?? throw new InvalidOperationException("No se pudo cargar la fuente del PDF.");
        using var negrita = SKTypeface.FromStream(negritaStream) ?? throw new InvalidOperationException("No se pudo cargar la fuente del PDF.");
        using var salida = new MemoryStream();
        using (var documento = SKDocument.CreatePdf(salida) ?? throw new InvalidOperationException("No se pudo crear el PDF."))
        using (var lienzo = new DibujantePdf(documento, fuente, negrita))
        {
            lienzo.ComenzarPagina();
            lienzo.ImagenCentrada(logo, 145, 65);
            lienzo.TextoCentrado(ajustes.NombreCompania, 15, true);
            lienzo.TextoCentrado($"RNC: {ajustes.RncCompania}", 10);
            lienzo.TextoCentrado($"Teléfonos: {ajustes.Telefonos}", 10);
            lienzo.TextoCentrado($"Correo: {ajustes.CorreoFactura}", 10);
            if (!string.IsNullOrWhiteSpace(ajustes.Direccion1)) lienzo.TextoCentrado(ajustes.Direccion1, 10);
            if (!string.IsNullOrWhiteSpace(ajustes.Direccion2)) lienzo.TextoCentrado(ajustes.Direccion2, 10);
            lienzo.Espacio(24);
            lienzo.Texto(datos.Titulo, 14, true);
            lienzo.Linea();
            lienzo.Texto($"Cliente: {datos.ClienteNombre}");
            lienzo.Texto($"RNC / Cédula: {Valor(datos.ClienteRnc)}");
            lienzo.Texto($"Teléfono: {Valor(cliente?.Telefono)}");
            lienzo.Espacio(8);
            if (datos.Ncf is not null) lienzo.Texto($"NCF: {datos.Ncf}", 10, true);
            var etiquetaFecha = datos.Ncf == "Borrador — NCF no asignado" ? "Fecha del borrador" : "Fecha de emisión";
            lienzo.Texto($"{etiquetaFecha}: {datos.Fecha:dd/MM/yyyy}");
            lienzo.Texto(datos.Titulo == "COTIZACIÓN"
                ? $"Válida hasta: {datos.Vencimiento:dd/MM/yyyy}"
                : $"Fecha de vencimiento: {(datos.Vencimiento is DateTime vencimiento ? vencimiento.ToString("dd/MM/yyyy") : "Sin vencimiento")}");
            lienzo.Espacio(14);
            lienzo.CabeceraTabla();
            foreach (var item in datos.Items) lienzo.Fila(item);
            lienzo.Espacio(8);
            lienzo.Total("Subtotal", datos.Subtotal);
            lienzo.Total("ITBIS", datos.Itbis);
            if (datos.Recargo != 0) lienzo.Total("Recargo", datos.Recargo);
            lienzo.Total("TOTAL", datos.Total, true);
            lienzo.Espacio(12);
            if (!string.IsNullOrWhiteSpace(datos.MetodoPago)) lienzo.Texto($"Método de pago: {datos.MetodoPago}");
            if (!string.IsNullOrWhiteSpace(datos.InstruccionPago)) lienzo.Texto($"Instrucciones de pago: {datos.InstruccionPago}");
            if (!string.IsNullOrWhiteSpace(datos.Nota)) lienzo.Texto($"Nota: {datos.Nota}");
            if (!string.IsNullOrWhiteSpace(datos.FirmaEmpresaRuta)) lienzo.Firma("Firma de la empresa", datos.FirmaEmpresaRuta);
            if (!string.IsNullOrWhiteSpace(datos.FirmaClienteRuta)) lienzo.Firma("Firma del cliente", datos.FirmaClienteRuta);
            documento.Close();
        }
        var ruta = Path.Combine(FileSystem.CacheDirectory, nombre);
        await File.WriteAllBytesAsync(ruta, salida.ToArray());
        return ruta;
    }

    private static string Valor(string? valor) => string.IsNullOrWhiteSpace(valor) ? "No disponible" : valor;

    private sealed record DocumentoPdf(string Titulo, string ClienteNombre, string ClienteRnc, DateTime Fecha,
        DateTime? Vencimiento, string? Ncf, List<ItemFactura> Items, decimal Subtotal, decimal Itbis,
        decimal Recargo, decimal Total, string Nota, string InstruccionPago, string MetodoPago,
        string FirmaEmpresaRuta, string FirmaClienteRuta);

    private sealed class DibujantePdf : IDisposable
    {
        private const float Ancho = 595, Alto = 842, Margen = 42, Pie = 45;
        private readonly SKDocument _documento;
        private readonly SKTypeface _fuente;
        private readonly SKTypeface _negrita;
        private readonly SKPaint _pintura = new() { IsAntialias = true, Color = SKColors.Black };
        private SKCanvas _canvas = null!;
        private float _y;
        private int _pagina;

        public DibujantePdf(SKDocument documento, SKTypeface fuente, SKTypeface negrita) =>
            (_documento, _fuente, _negrita) = (documento, fuente, negrita);

        public void ComenzarPagina()
        {
            if (_pagina > 0) _documento.EndPage();
            _canvas = _documento.BeginPage(Ancho, Alto);
            _pagina++;
            _y = Margen;
        }

        private void Asegurar(float alto) { if (_y + alto > Alto - Pie) ComenzarPagina(); }
        public void Espacio(float alto) { Asegurar(alto); _y += alto; }
        private static SKFont Fuente(SKTypeface fuente, float tamano) => new(fuente, tamano);

        private void Dibujar(string texto, float x, float y, SKFont fuente) =>
            _canvas.DrawText(texto, x, y, SKTextAlign.Left, fuente, _pintura);

        private static IEnumerable<string> Partir(string texto, SKFont fuente, float ancho)
        {
            foreach (var parrafo in texto.Replace("\r", "").Split('\n'))
            {
                var linea = string.Empty;
                foreach (var palabra in parrafo.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var restante = palabra;
                    while (restante.Length > 0)
                    {
                        var prefijo = linea.Length == 0 ? string.Empty : linea + " ";
                        var cantidad = restante.Length;
                        while (cantidad > 0 && fuente.MeasureText(prefijo + restante[..cantidad]) > ancho) cantidad--;
                        if (cantidad == 0 && linea.Length > 0) { yield return linea; linea = string.Empty; continue; }
                        if (cantidad == 0) cantidad = 1;
                        linea = prefijo + restante[..cantidad];
                        restante = restante[cantidad..];
                        if (restante.Length > 0) { yield return linea; linea = string.Empty; }
                    }
                }
                yield return linea;
            }
        }

        public void Texto(string texto, float tamano = 10, bool destacado = false)
        {
            using var fuente = Fuente(destacado ? _negrita : _fuente, tamano);
            foreach (var linea in Partir(texto, fuente, Ancho - 2 * Margen))
            {
                Asegurar(tamano + 6);
                _y += tamano + 4;
                Dibujar(linea, Margen, _y, fuente);
            }
        }

        public void TextoCentrado(string texto, float tamano, bool destacado = false)
        {
            using var fuente = Fuente(destacado ? _negrita : _fuente, tamano);
            foreach (var linea in Partir(texto, fuente, Ancho - 2 * Margen))
            {
                Asegurar(tamano + 6); _y += tamano + 4;
                Dibujar(linea, (Ancho - fuente.MeasureText(linea)) / 2, _y, fuente);
            }
        }

        public void ImagenCentrada(SKBitmap imagen, float anchoMaximo, float altoMaximo)
        {
            var escala = Math.Min(anchoMaximo / imagen.Width, altoMaximo / imagen.Height);
            var ancho = imagen.Width * escala; var alto = imagen.Height * escala;
            Asegurar(alto + 6);
            _canvas.DrawBitmap(imagen, new SKRect((Ancho - ancho) / 2, _y, (Ancho + ancho) / 2, _y + alto), _pintura);
            _y += alto + 6;
        }

        public void Linea()
        {
            Asegurar(10); _y += 5;
            using var trazo = new SKPaint { Color = new SKColor(200, 200, 200), StrokeWidth = 0.6f };
            _canvas.DrawLine(Margen, _y, Ancho - Margen, _y, trazo); _y += 5;
        }

        public void CabeceraTabla()
        {
            Asegurar(25);
            using var fondo = new SKPaint { Color = new SKColor(240, 243, 246) };
            _canvas.DrawRect(Margen, _y, Ancho - 2 * Margen, 22, fondo);
            using var fuente = Fuente(_negrita, 9);
            Dibujar("Artículo", Margen + 5, _y + 15, fuente);
            Dibujar("Cant.", 360, _y + 15, fuente);
            Dibujar("Precio", 415, _y + 15, fuente);
            Dibujar("Importe", 500, _y + 15, fuente);
            _y += 25;
        }

        public void Fila(ItemFactura item)
        {
            using var fuente = Fuente(_fuente, 9);
            var lineas = Partir(item.Descripcion, fuente, 295).ToList();
            for (var i = 0; i < lineas.Count; i++)
            {
                if (_y + 15 > Alto - Pie) { ComenzarPagina(); CabeceraTabla(); }
                _y += 14;
                Dibujar(lineas[i], Margen + 5, _y, fuente);
                if (i != 0) continue;
                Dibujar(item.Cantidad.ToString(CultureInfo.InvariantCulture), 360, _y, fuente);
                Dibujar(item.PrecioUnitario.ToString("N2", MonedaCultura), 415, _y, fuente);
                Dibujar(item.Subtotal.ToString("N2", MonedaCultura), 500, _y, fuente);
            }
            Linea();
        }

        public void Total(string etiqueta, decimal importe, bool destacado = false)
        {
            Asegurar(20);
            using var fuente = Fuente(destacado ? _negrita : _fuente, destacado ? 12 : 10);
            _y += 17;
            Dibujar(etiqueta, 350, _y, fuente);
            var valor = $"RD$ {importe.ToString("N2", MonedaCultura)}";
            Dibujar(valor, Ancho - Margen - fuente.MeasureText(valor), _y, fuente);
        }

        public void Firma(string etiqueta, string ruta)
        {
            if (!File.Exists(ruta)) throw new InvalidOperationException($"No se encuentra la imagen de {etiqueta.ToLowerInvariant()}.");
            using var imagen = SKBitmap.Decode(ruta) ?? throw new InvalidOperationException($"No se puede leer la imagen de {etiqueta.ToLowerInvariant()}.");
            Espacio(12); Texto(etiqueta, 10, true); ImagenCentrada(imagen, 120, 45);
        }

        public void Dispose() => _pintura.Dispose();
    }
}
