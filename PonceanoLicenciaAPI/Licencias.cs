using System.Collections.Concurrent;
using System.Net.Mail;

namespace PonceanoLicenciaAPI;

public sealed record Licencia(int Id, string CorreoUsuario, string EstadoLicencia, DateTimeOffset FechaVencimiento, string? CodigoTransaccionPago);
public sealed record ActivacionManual(string? CorreoUsuario, string? FechaVencimiento, string? CodigoTransaccionPago);

public static class Correo
{
    public static bool TryNormalizar(string? valor, out string correo)
    {
        correo = valor?.Trim() ?? string.Empty;
        if (correo.Length is 0 or > 254 || !MailAddress.TryCreate(correo, out var direccion) ||
            !string.Equals(direccion.Address, correo, StringComparison.OrdinalIgnoreCase))
            return false;
        correo = correo.ToLowerInvariant();
        return true;
    }
}

public interface ILicenciaRepository
{
    Task<Licencia?> BuscarAsync(string correo, CancellationToken cancellationToken);
    Task<Licencia> ActivarAsync(string correo, DateTimeOffset vencimiento, string codigo, CancellationToken cancellationToken);
}

public sealed class TransaccionDuplicadaException : Exception;

// Sustituir este repositorio por una implementación transaccional de base de datos antes de producción.
public sealed class LicenciaRepositoryMemoria : ILicenciaRepository
{
    private readonly ConcurrentDictionary<string, Licencia> _licencias = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Licencia> _transacciones = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private int _ultimoId;

    public Task<Licencia?> BuscarAsync(string correo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _licencias.TryGetValue(correo, out var licencia);
        return Task.FromResult(licencia);
    }

    public Task<Licencia> ActivarAsync(string correo, DateTimeOffset vencimiento, string codigo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_transacciones.TryGetValue(codigo, out var transaccion))
            {
                if (transaccion.CorreoUsuario == correo && transaccion.FechaVencimiento == vencimiento.ToUniversalTime())
                    return Task.FromResult(transaccion);
                throw new TransaccionDuplicadaException();
            }
            _licencias.TryGetValue(correo, out var anterior);
            var licencia = new Licencia(anterior?.Id ?? ++_ultimoId, correo, "Activo", vencimiento.ToUniversalTime(), codigo);
            _licencias[correo] = licencia;
            _transacciones[codigo] = licencia;
            return Task.FromResult(licencia);
        }
    }
}
