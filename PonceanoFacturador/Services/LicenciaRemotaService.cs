using System.Net.Http.Headers;
using System.Text.Json;

namespace PonceanoFacturador.Services;

public sealed record EstadoLicenciaRemota(string Estado, DateTimeOffset? Vencimiento);

public sealed class LicenciaRemotaService(HttpClient http)
{
    public const string Direccion = "https://ponceanomultiservicios-production.up.railway.app/api/licencia/verificar";

    public async Task<EstadoLicenciaRemota> VerificarAsync(string idToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, Direccion);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"El servidor de licencias respondió HTTP {(int)response.StatusCode}.");
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("estadoLicencia", out var estado) ||
            estado.ValueKind != JsonValueKind.String || estado.GetString() is not ("Activo" or "Pendiente" or "Vencido") ||
            !root.TryGetProperty("fechaVencimiento", out var fecha))
            throw new InvalidDataException("La respuesta del servidor de licencias no tiene el formato esperado.");
        DateTimeOffset? vencimiento = null;
        if (fecha.ValueKind != JsonValueKind.Null)
        {
            if (fecha.ValueKind != JsonValueKind.String || !fecha.TryGetDateTimeOffset(out var valor))
                throw new InvalidDataException("El vencimiento remoto no es válido.");
            vencimiento = valor.ToUniversalTime();
        }
        if (estado.GetString() == "Activo" && vencimiento is null)
            throw new InvalidDataException("La licencia remota activa no tiene vencimiento.");
        return new EstadoLicenciaRemota(estado.GetString()!, vencimiento);
    }
}
