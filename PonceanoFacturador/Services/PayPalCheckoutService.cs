using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PonceanoFacturador.Models;

namespace PonceanoFacturador.Services;

public sealed record OrdenCheckout(string Id, string Plan, decimal Importe, string Moneda, Uri UrlAprobacion);
public sealed record ResultadoPagoRemoto(string Estado, DateTimeOffset? Vencimiento);

public sealed class PayPalCheckoutService : IDisposable
{
    private const string DireccionOrden = "https://ponceanomultiservicios-production.up.railway.app/api/pago/orden";
    private readonly HttpClient _http;
    private readonly AuthService _auth;
    private readonly LicenciaRemotaService _licenciaRemota;
    private readonly DatabaseService _database;

    public PayPalCheckoutService(HttpClient http, AuthService auth, LicenciaRemotaService licenciaRemota, DatabaseService database)
    {
        _http = http;
        _auth = auth;
        _licenciaRemota = licenciaRemota;
        _database = database;
    }

    public async Task<OrdenCheckout> CrearOrdenAsync(string plan, CancellationToken cancellationToken = default)
    {
        var importe = plan switch
        {
            "mensual" => 35m,
            "anual" => 360m,
            _ => throw new ArgumentOutOfRangeException(nameof(plan), "Selecciona un plan disponible.")
        };
        var usuario = _auth.CurrentUser ?? throw new AuthException("Inicia sesión con un correo verificado antes de comprar.");
        var token = await _auth.ObtenerIdTokenActualizadoAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, DireccionOrden);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = System.Net.Http.Json.JsonContent.Create(new { plan });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (ReferenceEquals(usuario, _auth.CurrentUser) is false)
            throw new AuthException("La sesión cambió durante la creación del pago. Inténtalo otra vez.");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthException("La sesión Firebase venció. Inicia sesión de nuevo antes de pagar.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new AuthException("Verifica el correo de tu cuenta antes de comprar una licencia.");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            throw new InvalidOperationException("Los pagos Sandbox todavía no están configurados en el servidor. No se realizó ningún cobro.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"El servidor de pagos respondió HTTP {(int)response.StatusCode}.");

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        var id = LeerCadena(root, "ordenId");
        var planRespuesta = LeerCadena(root, "plan");
        var moneda = LeerCadena(root, "moneda");
        var url = LeerCadena(root, "urlAprobacion");
        if (id is null || id.Length is < 8 or > 64 || planRespuesta != plan || moneda != "USD" ||
            !root.TryGetProperty("importe", out var importeRespuesta) || !importeRespuesta.TryGetDecimal(out var importeRecibido) || importeRecibido != importe ||
            !Uri.TryCreate(url, UriKind.Absolute, out var aprobacion) || aprobacion.Scheme != Uri.UriSchemeHttps ||
            aprobacion.UserInfo.Length != 0 || aprobacion.Fragment.Length != 0 ||
            aprobacion.Host is not ("www.sandbox.paypal.com" or "www.paypal.com"))
            throw new InvalidDataException("La respuesta del servidor de pagos no es válida.");
        return new OrdenCheckout(id, planRespuesta, importeRecibido, moneda, aprobacion);
    }

    public async Task<ResultadoPagoRemoto> ActualizarLicenciaAsync(CancellationToken cancellationToken = default)
    {
        var usuario = _auth.CurrentUser ?? throw new AuthException("Inicia sesión para consultar la licencia.");
        var token = await _auth.ObtenerIdTokenActualizadoAsync(cancellationToken);
        var estado = await _licenciaRemota.VerificarAsync(token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(usuario, _auth.CurrentUser))
            throw new AuthException("La sesión cambió durante la consulta de licencia.");

        if (estado.Estado == "Activo" && estado.Vencimiento is DateTimeOffset vencimiento && vencimiento > DateTimeOffset.UtcNow)
        {
            var local = await _database.ObtenerConfiguracionLicenciaSinActualizarAsync(usuario.IdFirebase);
            _database.EstablecerLicenciaRemota(usuario.IdFirebase, vencimiento.UtcDateTime,
                !LicenciaService.LicenciaActiva(local), () => ReferenceEquals(usuario, _auth.CurrentUser));
        }
        else
        {
            _database.LimpiarLicenciaRemota();
        }
        return new ResultadoPagoRemoto(estado.Estado, estado.Vencimiento);
    }

    private static string? LeerCadena(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose() => _http.Dispose();
}
