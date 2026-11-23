using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PonceanoLicenciaAPI;

public enum ResultadoFirma { Valida, Invalida, NoDisponible }

public sealed class PayPalVerifier(HttpClient client, IConfiguration config, ILogger<PayPalVerifier> logger)
{
    public async Task<ResultadoFirma> VerificarAsync(IHeaderDictionary headers, JsonElement evento, CancellationToken cancellationToken)
    {
        var environment = config["PayPal:Environment"];
        var clientId = config["PayPal:ClientId"];
        var secret = config["PayPal:ClientSecret"];
        var webhookId = config["PayPal:WebhookId"];
        if (environment is not ("Sandbox" or "Live") || string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(webhookId))
        {
            logger.LogWarning("PayPal no está configurado para verificar firmas.");
            return ResultadoFirma.NoDisponible;
        }

        string? Header(string name) => headers.TryGetValue(name, out var values) && values.Count == 1 &&
            !string.IsNullOrWhiteSpace(values[0]) && values[0]!.Length <= 2048 ? values[0] : null;
        var authAlgo = Header("PAYPAL-AUTH-ALGO");
        var certUrl = Header("PAYPAL-CERT-URL");
        var transmissionId = Header("PAYPAL-TRANSMISSION-ID");
        var transmissionSig = Header("PAYPAL-TRANSMISSION-SIG");
        var transmissionTime = Header("PAYPAL-TRANSMISSION-TIME");
        var host = environment == "Sandbox" ? "api.sandbox.paypal.com" : "api.paypal.com";
        if (authAlgo != "SHA256withRSA" || transmissionId is null || transmissionSig is null ||
            transmissionTime is null || !DateTimeOffset.TryParse(transmissionTime, out _) ||
            !Uri.TryCreate(certUrl, UriKind.Absolute, out var cert) || cert.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(cert.Host, host, StringComparison.OrdinalIgnoreCase) || !cert.IsDefaultPort ||
            cert.UserInfo.Length != 0 || !cert.AbsolutePath.StartsWith("/v1/notifications/certs/", StringComparison.Ordinal) ||
            cert.Query.Length != 0 || cert.Fragment.Length != 0)
            return ResultadoFirma.Invalida;

        try
        {
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v1/oauth2/token");
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
            tokenRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
            using var tokenResponse = await client.SendAsync(tokenRequest, cancellationToken);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("No se pudo obtener un token de PayPal: HTTP {Status}.", (int)tokenResponse.StatusCode);
                return ResultadoFirma.NoDisponible;
            }
            using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken));
            if (!token.RootElement.TryGetProperty("access_token", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tokenValue.GetString()))
                return ResultadoFirma.NoDisponible;

            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v1/notifications/verify-webhook-signature");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenValue.GetString());
            request.Content = JsonContent.Create(new
            {
                auth_algo = authAlgo, cert_url = certUrl, transmission_id = transmissionId,
                transmission_sig = transmissionSig, transmission_time = transmissionTime,
                webhook_id = webhookId, webhook_event = evento
            });
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("PayPal no pudo verificar la firma: HTTP {Status}.", (int)response.StatusCode);
                return ResultadoFirma.NoDisponible;
            }
            using var result = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (!result.RootElement.TryGetProperty("verification_status", out var status) || status.ValueKind != JsonValueKind.String)
                return ResultadoFirma.NoDisponible;
            return status.GetString() switch
            {
                "SUCCESS" => ResultadoFirma.Valida,
                "FAILURE" => ResultadoFirma.Invalida,
                _ => ResultadoFirma.NoDisponible
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "No se pudo completar la verificación con PayPal.");
            return ResultadoFirma.NoDisponible;
        }
    }
}
