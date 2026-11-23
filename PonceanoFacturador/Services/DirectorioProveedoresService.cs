using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PonceanoFacturador.Services;

public sealed record ProveedorOcr(
    [property: JsonPropertyName("rnc")] string Rnc,
    [property: JsonPropertyName("razonSocial")] string RazonSocial,
    [property: JsonPropertyName("direccion")] string Direccion = "",
    [property: JsonPropertyName("telefono")] string Telefono = "");

public sealed class DirectorioProveedoresService
{
    private readonly string _ruta;
    private readonly ILogger<DirectorioProveedoresService> _logger;

    public DirectorioProveedoresService(ILogger<DirectorioProveedoresService> logger)
    {
        _logger = logger;
        _ruta = Path.Combine(RutasAplicacion.DirectorioDgii, "proveedores.json");
    }

    public async Task<Dictionary<string, ProveedorOcr>> CargarAsync()
    {
        if (!File.Exists(_ruta)) return new Dictionary<string, ProveedorOcr>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var stream = File.OpenRead(_ruta);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, ProveedorOcr>>(stream)
                ?? new Dictionary<string, ProveedorOcr>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo leer el directorio de proveedores OCR en {Ruta}.", _ruta);
            throw new InvalidDataException("No se pudo leer el directorio de proveedores. Verifica proveedores.json.", ex);
        }
    }

    public async Task<ProveedorOcr?> ObtenerAsync(string rnc)
    {
        if (string.IsNullOrWhiteSpace(rnc)) return null;
        var proveedores = await CargarAsync();
        proveedores.TryGetValue(rnc.Trim(), out var proveedor);
        return proveedor;
    }

    public async Task AgregarActualizarAsync(ProveedorOcr proveedor)
    {
        ArgumentNullException.ThrowIfNull(proveedor);
        if (string.IsNullOrWhiteSpace(proveedor.Rnc)) throw new ArgumentException("El RNC del proveedor es obligatorio.", nameof(proveedor));
        var proveedores = await CargarAsync();
        proveedores[proveedor.Rnc.Trim()] = proveedor with { Rnc = proveedor.Rnc.Trim() };
        await GuardarAsync(proveedores);
    }

    public async Task GuardarAsync(IReadOnlyDictionary<string, ProveedorOcr> proveedores)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_ruta)!);
            await using var stream = File.Create(_ruta);
            await JsonSerializer.SerializeAsync(stream, proveedores, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar el directorio de proveedores OCR en {Ruta}.", _ruta);
            throw new IOException("No se pudo guardar el directorio de proveedores.", ex);
        }
    }
}
