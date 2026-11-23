using System.Text.Json;
using System.Text.Json.Serialization;

namespace PonceanoFacturador.Services;

public sealed record ActualizacionDisponible(string Version, Uri Enlace);

public sealed class ActualizacionService
{
    public const string DireccionManifiesto = "https://ponceano49.github.io/ponceanomultiservicios/descargas_ponceano/version.json";
    private static readonly Uri ManifiestoCanonico = new("https://www.ponceanomultiservicios.com/descargas_ponceano/version.json");
    private readonly HttpClient _http;
    private readonly Uri _manifiesto;
    private int _consultada;

    public ActualizacionService(HttpClient http, Uri manifiesto)
    {
        _http = http;
        _manifiesto = manifiesto;
        if (!EsHttpsSeguro(manifiesto))
            throw new ArgumentException("La dirección del manifiesto debe ser HTTPS.", nameof(manifiesto));
    }

    public bool IniciarUnaVez() => Interlocked.Exchange(ref _consultada, 1) == 0;

    public async Task<ActualizacionDisponible?> ConsultarAsync(string versionActual, bool esWindows, CancellationToken cancellationToken = default)
    {
        if (!TryVersion(versionActual, out var actual))
            throw new InvalidDataException("La versión instalada no es válida.");

        var (respuesta, origenRespuesta) = await ObtenerManifiestoAsync(cancellationToken);
        using var response = respuesta;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536)
            throw new InvalidDataException("El manifiesto de actualización es demasiado grande.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int leidos;
        while ((leidos = await stream.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + leidos > 65536)
                throw new InvalidDataException("El manifiesto de actualización es demasiado grande.");
            buffer.Write(bytes, 0, leidos);
        }
        buffer.Position = 0;
        var datos = await JsonSerializer.DeserializeAsync<ManifiestoVersion>(buffer, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("El manifiesto de actualización está vacío.");
        if (!TryVersion(datos.UltimaVersion, out var ultima))
            throw new InvalidDataException("La última versión publicada no es válida.");
        if (ultima <= actual) return null;

        var enlace = esWindows ? datos.EnlaceWindows : datos.EnlaceAndroid;
        if (!Uri.TryCreate(enlace, UriKind.Absolute, out var uri) || !EsHttpsSeguro(uri) ||
            !MismoOrigen(uri, _manifiesto) && !MismoOrigen(uri, origenRespuesta) && !EsGoogleDrive(uri))
            throw new InvalidDataException("El enlace de descarga no pertenece a un servidor HTTPS autorizado.");
        return new(datos.UltimaVersion!, uri);
    }

    private async Task<(HttpResponseMessage Respuesta, Uri Origen)> ObtenerManifiestoAsync(CancellationToken cancellationToken)
    {
        var response = await EnviarAsync(_manifiesto, cancellationToken);
        if ((int)response.StatusCode is < 300 or >= 400) return (response, _manifiesto);
        var destino = response.Headers.Location;
        if (destino is not null && !destino.IsAbsoluteUri)
            destino = new Uri(_manifiesto, destino);
        if (_manifiesto != new Uri(DireccionManifiesto) || destino != ManifiestoCanonico)
        {
            response.Dispose();
            throw new InvalidDataException("La redirección del manifiesto no está autorizada.");
        }
        response.Dispose();
        return (await EnviarAsync(ManifiestoCanonico, cancellationToken), ManifiestoCanonico);
    }

    private async Task<HttpResponseMessage> EnviarAsync(Uri direccion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, direccion);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static bool MismoOrigen(Uri izquierda, Uri derecha) =>
        string.Equals(izquierda.Host, derecha.Host, StringComparison.OrdinalIgnoreCase) && izquierda.Port == derecha.Port;

    public static bool EsEnlaceDescargaPermitido(Uri uri) => EsHttpsSeguro(uri) && uri.IsDefaultPort &&
        (MismoOrigen(uri, new Uri(DireccionManifiesto)) || MismoOrigen(uri, ManifiestoCanonico) || EsGoogleDrive(uri));

    private static bool EsGoogleDrive(Uri uri) => EsHttpsSeguro(uri) && uri.IsDefaultPort &&
        (string.Equals(uri.Host, "drive.google.com", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "docs.google.com", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool EsHttpsSeguro(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static bool TryVersion(string? texto, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (!Version.TryParse(texto, out var valor) || valor.Major < 0 || valor.Minor < 0)
            return false;
        version = new Version(valor.Major, valor.Minor, Math.Max(valor.Build, 0), Math.Max(valor.Revision, 0));
        return true;
    }

    private sealed class ManifiestoVersion
    {
        [JsonPropertyName("ultima_version")]
        public string? UltimaVersion { get; set; }
        [JsonPropertyName("enlace_windows")]
        public string? EnlaceWindows { get; set; }
        [JsonPropertyName("enlace_android")]
        public string? EnlaceAndroid { get; set; }
    }
}
