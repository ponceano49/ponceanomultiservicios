using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PonceanoLicenciaAPI;

Check(PlanPago.TryObtener("mensual", out var mensual) && mensual.ImporteUsd == 35m && mensual.Dias == 30, "plan mensual USD 35 por 30 días");
Check(PlanPago.TryObtener("anual", out var anual) && anual.ImporteUsd == 360m && anual.Dias == 365, "plan anual USD 360 por 365 días");
Check(!PlanPago.TryObtener("otro", out _), "planes desconocidos rechazados");

var handler = new PayPalHandler();
using var client = new HttpClient(handler);
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["PayPal:Environment"] = "Sandbox",
    ["PayPal:ClientId"] = "sandbox-client",
    ["PayPal:ClientSecret"] = "sandbox-secret",
    ["PayPal:MerchantId"] = "merchant-123"
}).Build();
var paypal = new PayPalPaymentsClient(client, configuration, NullLogger<PayPalPaymentsClient>.Instance);
var created = await paypal.CrearOrdenAsync("reference-123", mensual, CancellationToken.None);
Check(created.Id == "ORDER12345678" && created.UrlAprobacion == "https://www.sandbox.paypal.com/checkoutnow?token=ORDER12345678", "orden Sandbox y URL de aprobación");
using (var requestBody = JsonDocument.Parse(handler.CreateOrderBody!))
{
    var unit = requestBody.RootElement.GetProperty("purchase_units")[0];
    Check(unit.GetProperty("amount").GetProperty("value").GetString() == "35.00" &&
        unit.GetProperty("amount").GetProperty("currency_code").GetString() == "USD" &&
        unit.GetProperty("custom_id").GetString() == "reference-123", "importe y referencia inmutables de la orden");
}

var verified = await paypal.CapturarYVerificarOrdenAsync("ORDER12345678", "CAPTURE123456", capturar: false, CancellationToken.None);
Check(verified is { IdOrden: "ORDER12345678", IdCaptura: "CAPTURE123456", Referencia: "reference-123", IdComerciante: "merchant-123", ImporteUsd: 35m, Moneda: "USD" }, "orden completada conciliada con comercio, referencia e importe");
Check(handler.Requests.Any(request => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/v2/checkout/orders/ORDER12345678", StringComparison.Ordinal)), "consulta autoritativa de orden para webhook");
Check(handler.Requests.All(request => request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/capture", StringComparison.Ordinal)), "webhook no vuelve a capturar el pago");

var liveConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["PayPal:Environment"] = "Live",
    ["PayPal:ClientId"] = "live-client",
    ["PayPal:ClientSecret"] = "live-secret",
    ["PayPal:MerchantId"] = "merchant-123"
}).Build();
var liveClient = new PayPalPaymentsClient(new HttpClient(new PayPalHandler()), liveConfig, NullLogger<PayPalPaymentsClient>.Instance);
try
{
    await liveClient.CrearOrdenAsync("reference-123", mensual, CancellationToken.None);
    throw new Exception("Live debe permanecer deshabilitado sin autorización explícita.");
}
catch (PayPalUnavailableException)
{
}
using var webhookEvent = JsonDocument.Parse("{}");
var liveVerifier = new PayPalVerifier(new HttpClient(new PayPalHandler()), liveConfig, NullLogger<PayPalVerifier>.Instance);
Check(await liveVerifier.VerificarAsync(new HeaderDictionary(), webhookEvent.RootElement, CancellationToken.None) == ResultadoFirma.NoDisponible,
    "webhooks Live permanecen deshabilitados sin autorización explícita");

Console.WriteLine("PayPalPaymentsChecks: pruebas superadas.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}

sealed class PayPalHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public string? CreateOrderBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/v1/oauth2/token", StringComparison.Ordinal))
            return Json("{\"access_token\":\"simulated-token\",\"token_type\":\"Bearer\"}");
        if (path.EndsWith("/v2/checkout/orders", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            CreateOrderBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("""
                {"id":"ORDER12345678","status":"CREATED","links":[{"rel":"approve","href":"https://www.sandbox.paypal.com/checkoutnow?token=ORDER12345678"}]}
                """);
        }
        if (path.EndsWith("/v2/checkout/orders/ORDER12345678", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            return Json("""
                {"id":"ORDER12345678","status":"COMPLETED","purchase_units":[{"custom_id":"reference-123","payee":{"merchant_id":"merchant-123"},"amount":{"currency_code":"USD","value":"35.00"},"payments":{"captures":[{"id":"CAPTURE123456","status":"COMPLETED","amount":{"currency_code":"USD","value":"35.00"}}]}}]}
                """);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
    };
}
