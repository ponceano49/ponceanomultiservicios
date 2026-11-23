using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
#if WINDOWS
using Microsoft.Win32;
#endif
using PonceanoFacturador.Models;

namespace PonceanoFacturador.Services;

public sealed class IdentificadorDispositivoService
{
    private const string InstalacionKey = "ponceano_device_installation_v1";

    public async Task<string> ObtenerIdAsync()
    {
#if WINDOWS
        using var clave = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var machineGuid = clave?.GetValue("MachineGuid") as string;
        if (string.IsNullOrWhiteSpace(machineGuid))
            throw new InvalidOperationException("Windows no proporcionó un identificador de dispositivo para vincular la licencia.");
        return HashId("windows-machine-guid", machineGuid);
#else
        var idInstalacion = await SecureStorage.Default.GetAsync(InstalacionKey);
        if (string.IsNullOrWhiteSpace(idInstalacion))
        {
            idInstalacion = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await SecureStorage.Default.SetAsync(InstalacionKey, idInstalacion);
        }
        return HashId("secure-installation-id", idInstalacion);
#endif
    }

    private static string HashId(string fuente, string identificador) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"PonceanoFacturador|{fuente}|{identificador}")));
}

public enum EstadoAccesoLicencia
{
    Permitido,
    RequiereActivacionPrueba,
    PruebaVencida,
    LicenciaPagadaVencida,
    DispositivoNoAutorizado,
    PruebaYaUsada
}

public sealed record EvaluacionLicencia(EstadoAccesoLicencia Estado, DateTime? VenceUtc = null);

public sealed class LicenciaPruebaService(
    DatabaseService database,
    IdentificadorDispositivoService dispositivo,
    ILogger<LicenciaPruebaService> logger)
{
    public const int DuracionPruebaDias = 30;
    public const string TipoPrueba = "Prueba gratuita 30 días";
    public const string CuentaLicenciaCompletaEspecial = AuthService.CorreoCuentaMaestra;
    public const string MensajeLicenciaCompletaActiva = "✅ LICENCIA COMPLETA ACTIVA (VÁLIDA POR 30 DÍAS)";
    public const string MensajePruebaFinalizada = "⚠️ PERÍODO DE PRUEBA DE 30 DÍAS FINALIZADO. FAVOR ADQUIRIR LA LICENCIA COMPLETA PARA DESBLOQUEAR PONCEANO FACTURADOR";
    public const string MensajeDispositivoNoAutorizado = "⚠️ LICENCIA NO AUTORIZADA PARA ESTA COMPUTADORA";
    public const string MensajePruebaYaUsada = "⚠️ La prueba gratuita ya está vinculada a otra cuenta de este dispositivo.";
    private const string BloqueoPruebaKey = "ponceano_trial_lock_v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record BloqueoPrueba(string UsuarioId, string HardwareId, DateTime InicioUtc, DateTime VencimientoUtc);

    public async Task<EvaluacionLicencia> EvaluarAsync(ConfiguracionUsuario estado, string usuarioId, DateTimeOffset horaNtpUtc, string? correo = null)
    {
        ArgumentNullException.ThrowIfNull(estado);
        ArgumentException.ThrowIfNullOrWhiteSpace(usuarioId);
        if (string.Equals(correo, CuentaLicenciaCompletaEspecial, StringComparison.OrdinalIgnoreCase))
            return new(EstadoAccesoLicencia.Permitido, estado.LicenciaVencimiento);
        var esGratis = estado.LicenciaTipo is "Gratuito" or "Gratuita";
        var esPrueba = estado.LicenciaTipo == TipoPrueba || (esGratis &&
            (estado.FechaInicioPruebaUtc.HasValue || !string.IsNullOrWhiteSpace(estado.IdentificadorHardware)));

        if (!esPrueba && !esGratis)
        {
            if (estado.LicenciaVencimiento is not DateTime vencimiento)
                return new(EstadoAccesoLicencia.LicenciaPagadaVencida);
            return horaNtpUtc.UtcDateTime < AsegurarUtc(vencimiento)
                ? new(EstadoAccesoLicencia.Permitido, AsegurarUtc(vencimiento))
                : new(EstadoAccesoLicencia.LicenciaPagadaVencida);
        }

        var hardwareId = await dispositivo.ObtenerIdAsync();
        var bloqueo = await LeerBloqueoAsync();
        if (esPrueba)
        {
            if (estado.FechaInicioPruebaUtc is not DateTime inicio || estado.FechaVencimientoPruebaUtc is not DateTime vencimientoPrueba ||
                !string.Equals(estado.IdentificadorHardware, hardwareId, StringComparison.Ordinal))
                return new(EstadoAccesoLicencia.DispositivoNoAutorizado);
            if (bloqueo is null || bloqueo.UsuarioId != usuarioId || bloqueo.HardwareId != hardwareId ||
                bloqueo.InicioUtc != AsegurarUtc(inicio) || bloqueo.VencimientoUtc != AsegurarUtc(vencimientoPrueba))
                return new(EstadoAccesoLicencia.DispositivoNoAutorizado);
            return horaNtpUtc.UtcDateTime < AsegurarUtc(vencimientoPrueba)
                ? new(EstadoAccesoLicencia.Permitido, AsegurarUtc(vencimientoPrueba))
                : new(EstadoAccesoLicencia.PruebaVencida);
        }

        if (bloqueo is null) return new(EstadoAccesoLicencia.RequiereActivacionPrueba);
        if (!string.Equals(bloqueo.HardwareId, hardwareId, StringComparison.Ordinal))
            return new(EstadoAccesoLicencia.DispositivoNoAutorizado);
        if (!string.Equals(bloqueo.UsuarioId, usuarioId, StringComparison.Ordinal))
            return new(EstadoAccesoLicencia.PruebaYaUsada);

        await database.ActivarPruebaGratuitaAsync(usuarioId, hardwareId, bloqueo.InicioUtc, bloqueo.VencimientoUtc);
        logger.LogInformation("Se recuperó el estado local de la prueba para {UsuarioId}.", usuarioId);
        return horaNtpUtc.UtcDateTime < bloqueo.VencimientoUtc
            ? new(EstadoAccesoLicencia.Permitido, bloqueo.VencimientoUtc)
            : new(EstadoAccesoLicencia.PruebaVencida);
    }

    public async Task<DateTime> ActivarAsync(string usuarioId, DateTimeOffset horaNtpUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usuarioId);
        var estado = await database.ObtenerConfiguracionLicenciaSinActualizarAsync(usuarioId);
        var acceso = await EvaluarAsync(estado, usuarioId, horaNtpUtc);
        if (acceso.Estado == EstadoAccesoLicencia.Permitido)
            return LicenciaPruebaService.AsegurarUtc(acceso.VenceUtc!.Value);
        if (acceso.Estado == EstadoAccesoLicencia.PruebaVencida)
            throw new InvalidOperationException(MensajePruebaFinalizada);
        if (acceso.Estado == EstadoAccesoLicencia.LicenciaPagadaVencida)
            throw new InvalidOperationException("⚠️ LICENCIA VENCIDA. Adquiere o renueva la licencia completa para continuar.");
        if (acceso.Estado == EstadoAccesoLicencia.DispositivoNoAutorizado)
            throw new InvalidOperationException(MensajeDispositivoNoAutorizado);
        if (acceso.Estado == EstadoAccesoLicencia.PruebaYaUsada)
            throw new InvalidOperationException(MensajePruebaYaUsada);

        var hardwareId = await dispositivo.ObtenerIdAsync();
        var bloqueo = await LeerBloqueoAsync();
        if (bloqueo is not null)
        {
            if (bloqueo.HardwareId != hardwareId) throw new InvalidOperationException(MensajeDispositivoNoAutorizado);
            if (bloqueo.UsuarioId != usuarioId) throw new InvalidOperationException(MensajePruebaYaUsada);
            await database.ActivarPruebaGratuitaAsync(usuarioId, hardwareId, bloqueo.InicioUtc, bloqueo.VencimientoUtc);
            return bloqueo.VencimientoUtc;
        }

        var inicio = horaNtpUtc.UtcDateTime;
        var vencimiento = inicio.AddDays(DuracionPruebaDias);
        var nuevaPrueba = new BloqueoPrueba(usuarioId, hardwareId, inicio, vencimiento);
        await SecureStorage.Default.SetAsync(BloqueoPruebaKey, JsonSerializer.Serialize(nuevaPrueba, JsonOptions));
        await database.ActivarPruebaGratuitaAsync(usuarioId, hardwareId, inicio, vencimiento);
        logger.LogInformation("Se activó la prueba gratuita de 30 días para {UsuarioId}; vence {VencimientoUtc}.", usuarioId, vencimiento);
        return vencimiento;
    }

#if DEBUG
    public async Task ReiniciarPruebaDebugAsync(string usuarioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usuarioId);
        await database.ReiniciarPruebaDebugAsync(usuarioId);
        SecureStorage.Default.Remove(BloqueoPruebaKey);
        if (!string.IsNullOrEmpty(await SecureStorage.Default.GetAsync(BloqueoPruebaKey)))
            throw new InvalidOperationException("No se pudo borrar el bloqueo de prueba del dispositivo. Inténtalo nuevamente.");
        logger.LogWarning("Se reinició la prueba local de desarrollo para {UsuarioId}.", usuarioId);
    }
#endif

    public static DateTime AsegurarUtc(DateTime valor) => valor.Kind switch
    {
        DateTimeKind.Utc => valor,
        DateTimeKind.Local => valor.ToUniversalTime(),
        _ => DateTime.SpecifyKind(valor, DateTimeKind.Utc)
    };

    private static async Task<BloqueoPrueba?> LeerBloqueoAsync()
    {
        var serializado = await SecureStorage.Default.GetAsync(BloqueoPruebaKey);
        if (string.IsNullOrWhiteSpace(serializado)) return null;
        try
        {
            var bloqueo = JsonSerializer.Deserialize<BloqueoPrueba>(serializado, JsonOptions)
                ?? throw new InvalidDataException("El registro seguro de la prueba está vacío.");
            if (string.IsNullOrWhiteSpace(bloqueo.UsuarioId) || string.IsNullOrWhiteSpace(bloqueo.HardwareId) || bloqueo.VencimientoUtc <= bloqueo.InicioUtc)
                throw new InvalidDataException("El registro seguro de la prueba no es válido.");
            return bloqueo with { InicioUtc = AsegurarUtc(bloqueo.InicioUtc), VencimientoUtc = AsegurarUtc(bloqueo.VencimientoUtc) };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("No se pudo leer el registro seguro de la prueba; contacta al soporte.", ex);
        }
    }
}
