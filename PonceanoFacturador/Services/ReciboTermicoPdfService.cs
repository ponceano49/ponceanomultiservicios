using System.Globalization;
using PonceanoFacturador.Models;
using SkiaSharp;

namespace PonceanoFacturador.Services;

public sealed class ReciboTermicoPdfService(DatabaseService db)
{
    private static readonly CultureInfo MonedaCultura = CultureInfo.GetCultureInfo("es-DO");
    private readonly DatabaseService _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<string> CrearAsync(Factura factura, string usuarioId)
    {
        ArgumentNullException.ThrowIfNull(factura);
        var ajustes = await _db.ObtenerAjustesAsync(usuarioId);
        if (string.IsNullOrWhiteSpace(ajustes.NombreCompania) || string.IsNullOrWhiteSpace(ajustes.RncCompania))
            throw new InvalidOperationException("Completa el nombre y RNC de la compañía en Ajustes antes de imprimir.");
        using var fuenteStream = await FileSystem.OpenAppPackageFileAsync("pdf-opensans.ttf");
        using var negritaStream = await FileSystem.OpenAppPackageFileAsync("pdf-opensans-semibold.ttf");
        using var fuente = SKTypeface.FromStream(fuenteStream) ?? throw new InvalidOperationException("No se pudo cargar la fuente del recibo.");
        using var negrita = SKTypeface.FromStream(negritaStream) ?? throw new InvalidOperationException("No se pudo cargar la fuente del recibo.");
        using var regular = new SKFont(fuente, 8);
        using var bold = new SKFont(negrita, 9);
        const float ancho = 226, margen = 12;
        var alto = 92f;
        alto += MedirLineas(ajustes.NombreCompania, bold, ancho - 2 * margen) * 11;
        alto += MedirLineas(factura.ClienteNombre, regular, ancho - 2 * margen) * 10;
        alto += factura.Items.Sum(item => MedirLineas(item.Descripcion, regular, ancho - 2 * margen) * 10 + 20);
        alto += 150;
        if (!string.IsNullOrWhiteSpace(factura.FirmaEmpresaRuta)) alto += 45;
        if (!string.IsNullOrWhiteSpace(factura.FirmaClienteRuta)) alto += 45;
        using var salida = new MemoryStream();
        using (var documento = SKDocument.CreatePdf(salida) ?? throw new InvalidOperationException("No se pudo crear el recibo PDF."))
        using (var canvas = new ReciboCanvas(documento, fuente, negrita, ancho, alto, margen))
        {
            using var logo = !string.IsNullOrWhiteSpace(ajustes.LogoRuta) && File.Exists(ajustes.LogoRuta) ? SKBitmap.Decode(ajustes.LogoRuta) : null;
            if (logo is not null) canvas.Imagen(logo, 82, 48);
            canvas.Centrado(ajustes.NombreCompania, 9, true);
            canvas.Centrado($"RNC: {ajustes.RncCompania}", 8);
            if (!string.IsNullOrWhiteSpace(ajustes.Telefonos)) canvas.Centrado(ajustes.Telefonos, 8);
            canvas.Linea();
            canvas.Texto(factura.TipoOperacion == "Compra" ? "RECIBO DE COMPRA" : "RECIBO / CUENTA DE COBRO", true);
            canvas.Texto($"NCF: {Valor(factura.NCF)}", true);
            canvas.Texto($"Fecha: {factura.Fecha.ToLocalTime():dd/MM/yyyy HH:mm}");
            canvas.Texto($"Cliente: {Valor(factura.ClienteNombre)}");
            canvas.Texto($"RNC/Cédula: {Valor(factura.ClienteRnc)}");
            canvas.Linea();
            foreach (var item in factura.Items)
            {
                canvas.Texto(item.Descripcion);
                canvas.Texto($"{item.Cantidad.ToString(CultureInfo.InvariantCulture)} x {Moneda(item.PrecioUnitario)} = {Moneda(item.Subtotal)}");
            }
            canvas.Linea();
            canvas.Importe("Subtotal", factura.Subtotal);
            canvas.Importe("ITBIS", factura.Itbis);
            if (factura.RecargoMonto != 0) canvas.Importe("Recargo", factura.RecargoMonto);
            canvas.Importe("TOTAL", factura.Total, true);
            canvas.Texto($"Estado: {factura.EstadoPago}");
            if (!string.IsNullOrWhiteSpace(factura.MetodoPago)) canvas.Texto($"Pago: {factura.MetodoPago}");
            if (!string.IsNullOrWhiteSpace(factura.Nota)) canvas.Texto($"Nota: {factura.Nota}");
            if (!string.IsNullOrWhiteSpace(factura.FirmaEmpresaRuta)) canvas.Firma("Firma de la empresa", factura.FirmaEmpresaRuta);
            if (!string.IsNullOrWhiteSpace(factura.FirmaClienteRuta)) canvas.Firma("Firma del cliente", factura.FirmaClienteRuta);
            canvas.Finalizar();
            documento.Close();
        }
        var ruta = Path.Combine(FileSystem.CacheDirectory, $"recibo-{factura.Id:N}.pdf");
        await File.WriteAllBytesAsync(ruta, salida.ToArray());
        return ruta;
    }

    private static string Valor(string? valor) => string.IsNullOrWhiteSpace(valor) ? "No disponible" : valor;
    private static string Moneda(decimal valor) => $"RD$ {valor.ToString("N2", MonedaCultura)}";
    private static int MedirLineas(string texto, SKFont fuente, float ancho)
    {
        var lineas = 0;
        foreach (var parrafo in texto.Replace("\r", string.Empty).Split('\n'))
        {
            var linea = string.Empty;
            foreach (var palabra in parrafo.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidata = linea.Length == 0 ? palabra : $"{linea} {palabra}";
                if (linea.Length > 0 && fuente.MeasureText(candidata) > ancho) { lineas++; linea = palabra; }
                else linea = candidata;
            }
            lineas++;
        }
        return Math.Max(1, lineas);
    }

    private sealed class ReciboCanvas : IDisposable
    {
        private readonly SKDocument _documento;
        private readonly SKTypeface _fuente;
        private readonly SKTypeface _negrita;
        private readonly float _ancho;
        private readonly float _alto;
        private readonly float _margen;
        private readonly SKPaint _pintura = new() { IsAntialias = true, Color = SKColors.Black };
        private readonly SKCanvas _canvas;
        private float _y;
        private bool _finalizado;

        public ReciboCanvas(SKDocument documento, SKTypeface fuente, SKTypeface negrita, float ancho, float alto, float margen)
        {
            _documento = documento; _fuente = fuente; _negrita = negrita; _ancho = ancho; _alto = alto; _margen = margen;
            _canvas = documento.BeginPage(ancho, alto);
            _y = margen;
        }

        public void Texto(string texto, bool destacado = false) => DibujarTexto(texto, destacado, false);
        public void Centrado(string texto, float tamano = 8, bool destacado = false) => DibujarTexto(texto, destacado, true, tamano);

        private void DibujarTexto(string texto, bool destacado, bool centrado, float tamano = 8)
        {
            using var fuente = new SKFont(destacado ? _negrita : _fuente, destacado && !centrado ? 9 : tamano);
            var linea = string.Empty;
            foreach (var palabra in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidata = linea.Length == 0 ? palabra : $"{linea} {palabra}";
                if (linea.Length > 0 && fuente.MeasureText(candidata) > _ancho - 2 * _margen)
                {
                    EscribirLinea(linea, fuente, centrado); linea = palabra;
                }
                else linea = candidata;
            }
            if (linea.Length > 0) EscribirLinea(linea, fuente, centrado);
        }

        private void EscribirLinea(string linea, SKFont fuente, bool centrado)
        {
            _y += fuente.Size + 3;
            var x = centrado ? (_ancho - fuente.MeasureText(linea)) / 2 : _margen;
            _canvas.DrawText(linea, x, _y, SKTextAlign.Left, fuente, _pintura);
        }

        public void Imagen(SKBitmap imagen, float anchoMaximo, float altoMaximo)
        {
            var escala = Math.Min(anchoMaximo / imagen.Width, altoMaximo / imagen.Height);
            var ancho = imagen.Width * escala; var alto = imagen.Height * escala;
            _canvas.DrawBitmap(imagen, new SKRect((_ancho - ancho) / 2, _y, (_ancho + ancho) / 2, _y + alto), _pintura);
            _y += alto + 4;
        }

        public void Linea()
        {
            _y += 4;
            using var trazo = new SKPaint { Color = SKColors.Black, StrokeWidth = 0.6f };
            _canvas.DrawLine(_margen, _y, _ancho - _margen, _y, trazo);
            _y += 4;
        }

        public void Importe(string etiqueta, decimal importe, bool destacado = false)
        {
            using var fuente = new SKFont(destacado ? _negrita : _fuente, destacado ? 10 : 8);
            _y += fuente.Size + 4;
            var valor = Moneda(importe);
            _canvas.DrawText(etiqueta, _margen, _y, SKTextAlign.Left, fuente, _pintura);
            _canvas.DrawText(valor, _ancho - _margen - fuente.MeasureText(valor), _y, SKTextAlign.Left, fuente, _pintura);
        }

        public void Firma(string etiqueta, string ruta)
        {
            if (!File.Exists(ruta)) throw new InvalidOperationException($"No se encuentra la imagen de {etiqueta.ToLowerInvariant()}.");
            using var imagen = SKBitmap.Decode(ruta) ?? throw new InvalidOperationException($"No se puede leer la imagen de {etiqueta.ToLowerInvariant()}.");
            Texto(etiqueta, true); Imagen(imagen, 100, 36);
        }

        public void Finalizar()
        {
            if (_finalizado) return;
            _documento.EndPage();
            _finalizado = true;
        }

        public void Dispose() { Finalizar(); _pintura.Dispose(); }
    }
}
