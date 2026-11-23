using System.Runtime.CompilerServices;
using System.Text;
using Tesseract;

namespace PonceanoFacturador.Services;

public sealed class TesseractOcrService(IPreprocesadorFacturaOcr preprocesador)
{
    private const int MaximoPaginas = 50;
    private readonly IPreprocesadorFacturaOcr _preprocesador = preprocesador ?? throw new ArgumentNullException(nameof(preprocesador));

    public async Task<string> LeerTextoDeImagenAsync(string rutaImagen, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rutaImagen);
        var ruta = Path.GetFullPath(rutaImagen);
        if (!File.Exists(ruta)) throw new FileNotFoundException("No se encontró el archivo seleccionado.", ruta);
        if (Path.GetExtension(ruta).ToLowerInvariant() is not (".pdf" or ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp"))
            throw new InvalidDataException("Formato no compatible. Selecciona un PDF, JPG, PNG, TIFF o BMP.");

        var carpetaIdioma = Path.Combine(AppContext.BaseDirectory, "tessdata");
        var archivoIdioma = Path.Combine(carpetaIdioma, "spa.traineddata");
        if (!File.Exists(archivoIdioma))
            throw new FileNotFoundException($"⚠️ Falta el archivo de idioma español. Descárgalo y colócalo en la carpeta tessdata del programa: {archivoIdioma}", archivoIdioma);

#if !WINDOWS
        throw new PlatformNotSupportedException("La lectura Tesseract de imágenes y PDF está habilitada actualmente para Windows.");
#else
        var texto = new StringBuilder();
        var paginas = 0;
        using var engine = new TesseractEngine(carpetaIdioma, "spa", EngineMode.Default);
        await foreach (var imagen in _preprocesador.PrepararPaginasAsync(ruta, cancellationToken).WithCancellation(cancellationToken))
        {
            if (++paginas > MaximoPaginas) throw new InvalidDataException($"El documento supera el límite de {MaximoPaginas} páginas.");
            cancellationToken.ThrowIfCancellationRequested();
            using var pixeles = Pix.LoadFromMemory(imagen);
            using var pagina = await Task.Run(() => engine.Process(pixeles), cancellationToken);
            if (texto.Length > 0) texto.AppendLine().AppendLine("--- Página siguiente ---").AppendLine();
            texto.Append(pagina.GetText());
        }
        if (paginas == 0) throw new InvalidDataException("El archivo no contiene páginas para procesar.");
        if (string.IsNullOrWhiteSpace(texto.ToString())) throw new InvalidDataException("Tesseract no encontró texto legible en el documento.");
        return texto.ToString();
#endif
    }

    public async Task<string> GuardarLecturaAsync(string nombreOriginal, string texto, CancellationToken cancellationToken = default, string? resumenMoneda = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nombreOriginal);
        ArgumentNullException.ThrowIfNull(texto);
        var carpeta = Path.Combine(AppContext.BaseDirectory, "Reportes", "OCR", "Leidos");
        var fecha = DateTime.Now;
        var prefijo = $"Lectura_{fecha:yyyy-MM-dd_HH-mm-ss}";
        string? ruta = null;
        try
        {
            Directory.CreateDirectory(carpeta);
            var baseNombre = prefijo;
            var indice = 2;
            ruta = Path.Combine(carpeta, baseNombre + ".txt");
            while (File.Exists(ruta)) ruta = Path.Combine(carpeta, $"{baseNombre}_{indice++}.txt");
            var contenido = new StringBuilder()
                .AppendLine($"Archivo: {Path.GetFileName(nombreOriginal)}")
                .AppendLine($"Fecha y hora: {fecha:yyyy-MM-dd HH:mm:ss}")
                .AppendLine()
                .AppendLine(texto)
                .ToString();
            if (!string.IsNullOrWhiteSpace(resumenMoneda))
                contenido += $"{Environment.NewLine}--- MONEDA Y TASA DE CAMBIO ---{Environment.NewLine}{resumenMoneda}{Environment.NewLine}";
            await File.WriteAllTextAsync(ruta, contenido, new UTF8Encoding(false), cancellationToken);
            return ruta;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rutaIntentada = ruta ?? Path.Combine(carpeta, prefijo + ".txt");
            throw new IOException($"No se pudo guardar la lectura OCR en '{rutaIntentada}': {ex.Message}", ex);
        }
    }
}
