using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PonceanoLicenciaAPI;

public sealed record PlanPago(string Codigo, string Nombre, decimal ImporteUsd, int Dias)
{
    public static bool TryObtener(string? codigo, out PlanPago plan)
    {
        plan = codigo switch
        {
            "mensual" => new PlanPago("mensual", "Plan mensual PonceanoFacturador", 35m, 30),
            "anual" => new PlanPago("anual", "Plan anual PonceanoFacturador", 360m, 365),
            _ => null!
        };
        return plan is not null;
    }
}

public sealed record OrdenPayPal(string Id, string Referencia, string Correo, PlanPago Plan, string Estado);
public sealed record OrdenPayPalCreada(string Id, string UrlAprobacion);
public sealed record SolicitudOrdenPago(string? Plan);
public sealed record PagoPayPalVerificado(string? IdEvento, string IdCaptura, string IdOrden, string Referencia,
    string IdComerciante, decimal ImporteUsd, string Moneda);
public enum ResultadoProcesamientoPago { Activado, YaProcesado, OrdenDesconocida, DatosNoCoinciden }

public interface IPayPalPaymentStore
{
    Task RegistrarOrdenAsync(string idOrden, string referencia, string correo, PlanPago plan, CancellationToken cancellationToken);
    Task<OrdenPayPal?> BuscarOrdenAsync(string idOrden, CancellationToken cancellationToken);
    Task<ResultadoProcesamientoPago> ProcesarPagoAsync(PagoPayPalVerificado pago, CancellationToken cancellationToken);
}

public sealed class PayPalPaymentsClient(HttpClient client, IConfiguration configuration, ILogger<PayPalPaymentsClient> logger)
{
    private const string SandboxHost = "api-m.sandbox.paypal.com";
    private const string LiveHost = "api-m.paypal.com";
    private static readonly Regex IdPayPalRegex = new("^[A-Za-z0-9_-]{8,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<OrdenPayPalCreada> CrearOrdenAsync(string referencia, PlanPago plan, CancellationToken cancellationToken)
    {
        var (host, token) = await ObtenerTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v2/checkout/orders");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("PayPal-Request-Id", Guid.NewGuid().ToString("N"));
        request.Content = JsonContent.Create(new
        {
            intent = "CAPTURE",
            purchase_units = new[]
            {
                new
                {
                    reference_id = referencia,
                    custom_id = referencia,
                    description = plan.Nombre,
                    amount = new { currency_code = "USD", value = plan.ImporteUsd.ToString("F2", CultureInfo.InvariantCulture) }
                }
            },
            application_context = new
            {
                brand_name = "Ponceano Multiservicios",
                user_action = "PAY_NOW",
                return_url = "https://ponceanomultiservicios-production.up.railway.app/api/pago/retorno",
                cancel_url = "https://ponceanomultiservicios-production.up.railway.app/api/pago/cancelado"
            }
        });

        using var response = await EnviarAsync(request, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var id = LeerCadena(root, "id");
        var approval = root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
            ? links.EnumerateArray().Where(link => LeerCadena(link, "rel") is "approve" or "payer-action")
                .Select(link => LeerCadena(link, "href")).FirstOrDefault(href => href is not null)
            : null;
        var expectedApprovalHost = configuration["PayPal:Environment"] == "Sandbox" ? "www.sandbox.paypal.com" : "www.paypal.com";
        if (LeerCadena(root, "status") != "CREATED" || id is null || !IdPayPalRegex.IsMatch(id) || approval is null ||
            !Uri.TryCreate(approval, UriKind.Absolute, out var approvalUri) || approvalUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(approvalUri.Host, expectedApprovalHost, StringComparison.OrdinalIgnoreCase) || approvalUri.UserInfo.Length != 0 || approvalUri.Fragment.Length != 0)
            throw new PayPalUnavailableException("PayPal no devolvió una orden de aprobación válida.");
        return new OrdenPayPalCreada(id, approval);
    }

    public async Task<PagoPayPalVerificado?> CapturarYVerificarOrdenAsync(string idOrden, string? idCapturaEsperado, bool capturar, CancellationToken cancellationToken)
    {
        if (!IdPayPalRegex.IsMatch(idOrden))
            return null;

        var (host, token) = await ObtenerTokenAsync(cancellationToken);
        if (capturar)
        {
            using var captureRequest = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v2/checkout/orders/{Uri.EscapeDataString(idOrden)}/capture");
            captureRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            captureRequest.Headers.Add("PayPal-Request-Id", $"capture-{idOrden}");
            captureRequest.Content = JsonContent.Create(new { });
            using var captureResponse = await client.SendAsync(captureRequest, cancellationToken);
            if (!captureResponse.IsSuccessStatusCode)
                logger.LogInformation("La captura PayPal respondió HTTP {Status}; se consultará el estado actual de la orden.", (int)captureResponse.StatusCode);
        }

        using var query = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/v2/checkout/orders/{Uri.EscapeDataString(idOrden)}");
        query.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await EnviarAsync(query, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (LeerCadena(root, "id") != idOrden || LeerCadena(root, "status") != "COMPLETED" ||
            !root.TryGetProperty("purchase_units", out var units) || units.ValueKind != JsonValueKind.Array || units.GetArrayLength() != 1)
            return null;

        var unit = units[0];
        if (unit.ValueKind != JsonValueKind.Object)
            return null;
        var reference = LeerCadena(unit, "custom_id");
        var merchantId = unit.TryGetProperty("payee", out var payee) ? LeerCadena(payee, "merchant_id") : null;
        if (reference is null || merchantId is null || !string.Equals(merchantId, configuration["PayPal:MerchantId"], StringComparison.Ordinal))
            return null;
        if (!LeerImporte(unit, out var orderAmount, out var orderCurrency) ||
            !unit.TryGetProperty("payments", out var payments) || payments.ValueKind != JsonValueKind.Object ||
            !payments.TryGetProperty("captures", out var captures) || captures.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var capture in captures.EnumerateArray())
        {
            if (capture.ValueKind != JsonValueKind.Object)
                continue;
            var captureId = LeerCadena(capture, "id");
            if (LeerCadena(capture, "status") != "COMPLETED" || captureId is null || !IdPayPalRegex.IsMatch(captureId) ||
                idCapturaEsperado is not null && !string.Equals(captureId, idCapturaEsperado, StringComparison.Ordinal) ||
                !LeerImporte(capture, out var captureAmount, out var captureCurrency) ||
                captureAmount != orderAmount || !string.Equals(captureCurrency, orderCurrency, StringComparison.Ordinal))
                continue;
            return new PagoPayPalVerificado(null, captureId, idOrden, reference, merchantId, captureAmount, captureCurrency);
        }
        return null;
    }

    private async Task<(string Host, string Token)> ObtenerTokenAsync(CancellationToken cancellationToken)
    {
        var environment = configuration["PayPal:Environment"];
        var clientId = configuration["PayPal:ClientId"];
        var secret = configuration["PayPal:ClientSecret"];
        var merchantId = configuration["PayPal:MerchantId"];
        if (environment is not ("Sandbox" or "Live") || string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(merchantId) ||
            environment == "Live" && !configuration.GetValue<bool>("PayPal:AllowLivePayments"))
            throw new PayPalUnavailableException("PayPal no está configurado para procesar pagos en el entorno permitido.");

        var host = environment == "Sandbox" ? SandboxHost : LiveHost;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v1/oauth2/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
        using var response = await EnviarAsync(request, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var token = LeerCadena(document.RootElement, "access_token");
        if (token is null)
            throw new PayPalUnavailableException("PayPal no devolvió un token de acceso válido.");
        return (host, token);
    }

    private async Task<HttpResponseMessage> EnviarAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                logger.LogWarning("La API de PayPal respondió HTTP {Status}.", status);
                throw new PayPalUnavailableException("La solicitud a PayPal no se pudo completar.");
            }
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("No se pudo contactar con PayPal ({Tipo}).", ex.GetType().Name);
            throw new PayPalUnavailableException("PayPal no está disponible temporalmente.");
        }
    }

    private static string? LeerCadena(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool LeerImporte(JsonElement element, out decimal amount, out string currency)
    {
        amount = 0;
        currency = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("amount", out var value) || value.ValueKind != JsonValueKind.Object ||
            !decimal.TryParse(LeerCadena(value, "value"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
            return false;
        currency = LeerCadena(value, "currency_code") ?? string.Empty;
        return currency.Length == 3;
    }
}

public sealed class PayPalUnavailableException(string message) : Exception(message);

public static class PayPalJsonExtensions
{
    public static string? GetPropertyOrDefault(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
