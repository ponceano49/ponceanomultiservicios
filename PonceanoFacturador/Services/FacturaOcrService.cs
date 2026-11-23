using System.Net.Http.Headers;
using System.Text.Json;

namespace PonceanoFacturador.Services;

public sealed class OcrSpaceException(string message) : Exception(message);

public sealed class FacturaOcrService(HttpClient httpClient, IPreprocesadorFacturaOcr? preprocesador = null) : IDisposable
{
    private const string ApiUrl = "https://api.ocr.space/parse/image";
    private const int MaximoBytesPagina = 950 * 1024;
    private const string ApiKeyVariable = "DGII_OCR_SPACE_API_KEY";
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IPreprocesadorFacturaOcr _preprocesador = preprocesador ?? new PreprocesadorFacturaOcr();

    public bool EstaConfigurado => !string.IsNullOrWhiteSpace(ObtenerApiKey());

    public Task<string> LeerTextoDesdePDFAsync(string ruta)
    {
        if (!Path.GetExtension(ruta).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("El archivo debe ser un PDF.", nameof(ruta));
        return ExtraerTextoAsync(ruta);
    }

    public Task<string> LeerTextoConOCRAsync(string ruta) => ExtraerTextoAsync(ruta);

    public async Task<string> ExtraerTextoAsync(string ruta, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruta);
        var apiKey = ObtenerApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"Configura la variable de entorno de usuario {ApiKeyVariable} y reinicia la aplicación.");
        if (!File.Exists(ruta)) throw new FileNotFoundException("No se encontró el archivo para procesar.", ruta);

        var extension = Path.GetExtension(ruta);
        if (!new[] { ".pdf", ".jpg", ".jpeg", ".png" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Solo se admiten archivos PDF, JPG o PNG.");

        var paginas = new List<string>();
        await foreach (var imagen in _preprocesador.PrepararPaginasAsync(ruta, cancellationToken).WithCancellation(cancellationToken))
        {
            paginas.Add(await ExtraerPaginaAsync(imagen, apiKey, cancellationToken));
        }
        if (paginas.Count == 0) throw new InvalidDataException("El archivo no contiene páginas para procesar.");
        return string.Join(Environment.NewLine, paginas);
    }

    private async Task<string> ExtraerPaginaAsync(byte[] imagen, string apiKey, CancellationToken cancellationToken)
    {
        if (imagen.Length > MaximoBytesPagina)
            throw new InvalidDataException("La página preparada supera el límite seguro de 1 MB para OCR.space.");

        using var contenido = new MultipartFormDataContent();
        contenido.Add(new StringContent("spa"), "language");
        contenido.Add(new StringContent("false"), "isOverlayRequired");
        contenido.Add(new StringContent("2"), "OCREngine");
        contenido.Add(new StringContent("true"), "detectOrientation");
        using var archivoContenido = new ByteArrayContent(imagen);
        archivoContenido.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        contenido.Add(archivoContenido, "file", "pagina.jpg");

        using var solicitud = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = contenido };
        solicitud.Headers.TryAddWithoutValidation("apikey", apiKey);
        using var respuesta = await _httpClient.SendAsync(solicitud, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!respuesta.IsSuccessStatusCode)
            throw new HttpRequestException($"OCR.space respondió con HTTP {(int)respuesta.StatusCode} ({respuesta.ReasonPhrase}).", null, respuesta.StatusCode);

        await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(flujo, cancellationToken: cancellationToken);
        var raiz = json.RootElement;
        if (raiz.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("OCR.space devolvió una respuesta JSON no válida.");
        if (raiz.TryGetProperty("IsErroredOnProcessing", out var errorProcesamiento) &&
            (errorProcesamiento.ValueKind == JsonValueKind.True ||
             errorProcesamiento.ValueKind == JsonValueKind.String && bool.TryParse(errorProcesamiento.GetString(), out var hayError) && hayError))
            throw new OcrSpaceException("OCR.space rechazó la solicitud. Comprueba la clave, los límites del servicio y el archivo.");

        if (!raiz.TryGetProperty("ParsedResults", out var resultados) || resultados.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(LeerMensajeError(raiz) ?? "OCR.space no devolvió resultados para el archivo.");

        var paginasResultado = resultados.EnumerateArray().ToList();
        if (paginasResultado.Any(resultado => resultado.ValueKind != JsonValueKind.Object ||
            resultado.TryGetProperty("FileParseExitCode", out var codigo) && codigo.ValueKind == JsonValueKind.Number && codigo.TryGetInt32(out var valor) && valor != 1))
            throw new OcrSpaceException("OCR.space no pudo leer todas las páginas del documento.");
        var paginas = paginasResultado
            .Select(resultado => resultado.TryGetProperty("ParsedText", out var texto) && texto.ValueKind == JsonValueKind.String ? texto.GetString() : null)
            .ToList();
        if (paginas.Count == 0 || paginas.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException(LeerMensajeError(raiz) ?? "OCR.space no encontró texto legible en todas las páginas del archivo.");
        return string.Join(Environment.NewLine, paginas);
    }

    public void Dispose() => _httpClient.Dispose();

    private static string? ObtenerApiKey()
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
#if WINDOWS
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable, EnvironmentVariableTarget.User);
#endif
        return apiKey;
    }

    private static string? LeerMensajeError(JsonElement raiz)
    {
        if (!raiz.TryGetProperty("ErrorMessage", out var mensaje)) return null;
        if (mensaje.ValueKind == JsonValueKind.String) return mensaje.GetString();
        if (mensaje.ValueKind == JsonValueKind.Array)
            return string.Join(" ", mensaje.EnumerateArray().Where(elemento => elemento.ValueKind == JsonValueKind.String).Select(elemento => elemento.GetString()));
        return null;
    }
}
