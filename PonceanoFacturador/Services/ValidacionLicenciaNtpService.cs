using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;

namespace PonceanoFacturador.Services;

public interface IRelojNtpService
{
    Task<DateTimeOffset> ObtenerHoraUtcAsync(CancellationToken cancellationToken = default);
}

public sealed class RelojNtpService(ILogger<RelojNtpService> logger) : IRelojNtpService
{
    private static readonly string[] Servidores = ["time.cloudflare.com", "time.google.com", "pool.ntp.org"];
    private static readonly TimeSpan TiempoLimitePorServidor = TimeSpan.FromSeconds(4);
    private const long DiferenciaEpocaNtpUnix = 2_208_988_800L;

    public async Task<DateTimeOffset> ObtenerHoraUtcAsync(CancellationToken cancellationToken = default)
    {
        var consultas = await Task.WhenAll(Servidores.Select(servidor => ConsultarServidorAsync(servidor, cancellationToken)));
        var respuestas = consultas.Where(hora => hora.HasValue).Select(hora => hora!.Value.UtcDateTime.Ticks).Order().ToArray();
        if (respuestas.Length < 2)
            throw new InvalidOperationException("No se pudo confirmar la hora con al menos dos servidores NTP públicos.");
        if (TimeSpan.FromTicks(respuestas[^1] - respuestas[0]) > TimeSpan.FromSeconds(10))
            throw new InvalidOperationException("Los servidores NTP públicos devolvieron horas inconsistentes.");

        return new DateTimeOffset(respuestas[respuestas.Length / 2], TimeSpan.Zero);
    }

    private async Task<DateTimeOffset?> ConsultarServidorAsync(string servidor, CancellationToken cancellationToken)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(TiempoLimitePorServidor);
        try
        {
            var direcciones = await Dns.GetHostAddressesAsync(servidor, limite.Token);
            var direccion = direcciones.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            using var cliente = new UdpClient(AddressFamily.InterNetwork);
            cliente.Connect(new IPEndPoint(direccion, 123));

            var solicitud = new byte[48];
            solicitud[0] = 0x23;
            var nonce = RandomNumberGenerator.GetBytes(8);
            nonce.CopyTo(solicitud, 40);
            await cliente.SendAsync(solicitud, limite.Token);

            var respuesta = await cliente.ReceiveAsync(limite.Token);
            if (respuesta.Buffer.Length < 48)
                throw new InvalidDataException("La respuesta NTP está incompleta.");

            var modo = respuesta.Buffer[0] & 0x07;
            var indicadorSalto = respuesta.Buffer[0] >> 6;
            var estrato = respuesta.Buffer[1];
            if (modo != 4 || indicadorSalto == 3 || estrato is 0 or > 15 || !respuesta.Buffer.AsSpan(24, 8).SequenceEqual(nonce))
                throw new InvalidDataException("La respuesta no es una respuesta NTP válida para esta solicitud.");

            var segundosNtp = BinaryPrimitives.ReadUInt32BigEndian(respuesta.Buffer.AsSpan(40, 4));
            var fraccion = BinaryPrimitives.ReadUInt32BigEndian(respuesta.Buffer.AsSpan(44, 4));
            if (segundosNtp == 0) throw new InvalidDataException("El servidor NTP devolvió una marca de tiempo vacía.");

            var segundosUnix = (long)segundosNtp - DiferenciaEpocaNtpUnix;
            var ticksFraccionarios = (long)(((ulong)fraccion * TimeSpan.TicksPerSecond) >> 32);
            return DateTimeOffset.FromUnixTimeSeconds(segundosUnix).AddTicks(ticksFraccionarios);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("La consulta al servidor NTP {Servidor} excedió el tiempo límite.", servidor);
            return null;
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "No se pudo consultar el servidor NTP {Servidor}.", servidor);
            return null;
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "El servidor NTP {Servidor} devolvió una respuesta inválida.", servidor);
            return null;
        }
    }
}

public sealed record ResultadoValidacionLicenciaInicial(bool Permitida, string Mensaje, bool RequiereActivacionPrueba = false, DateTime? VenceUtc = null, bool AdministradorSinNtp = false, bool AccesoPruebaDebug = false);

public sealed class ValidacionLicenciaNtpService(
    AuthService auth,
    DatabaseService database,
    IRelojNtpService relojNtp,
    LicenciaPruebaService licenciaPrueba,
    ILogger<ValidacionLicenciaNtpService> logger,
    LicenciaRemotaService licenciaRemota)
{
    public const string MensajeSinConexion = "⚠️ SE REQUIERE CONEXIÓN A INTERNET PARA VALIDAR LA LICENCIA DE PONCEANO FACTURADOR";
    public const string MensajeLicenciaVencida = "⚠️ LICENCIA VENCIDA, FAVOR RENOVAR LA LICENCIA";
    public const string MensajeAdministradorSinNtp = "⚠️ Modo Administrador Offline activado (sin validación NTP, solo para gestión interna). La gestión centralizada de licencias aún no está disponible.";

    public async Task<ResultadoValidacionLicenciaInicial> ValidarAntesDeEntrarAsync(CancellationToken cancellationToken = default)
    {
        var usuarioId = auth.CurrentUser?.IdFirebase;
        if (string.IsNullOrWhiteSpace(usuarioId))
            throw new InvalidOperationException("Inicia sesión antes de validar la licencia.");

#if DEBUG
        if (AuthService.EsAccesoPruebaDebug(auth.CurrentUser?.Correo))
        {
            cancellationToken.ThrowIfCancellationRequested();
            const string mensajePrueba = "Modo de prueba Debug: se omiten verificación de correo y activación de licencia; no se activa una licencia comercial ni una prueba persistente.";
            logger.LogWarning("Se concedió acceso temporal Debug a {Correo}; no se validó ni activó una licencia.", auth.CurrentUser?.Correo);
            return new(true, mensajePrueba, AccesoPruebaDebug: true);
        }
#endif

        var esAdministrador = string.Equals(auth.CurrentUser?.Correo, LicenciaPruebaService.CuentaLicenciaCompletaEspecial, StringComparison.OrdinalIgnoreCase);
        ResultadoValidacionLicenciaInicial SinHoraNtp() => esAdministrador
            ? new(true, MensajeAdministradorSinNtp, AdministradorSinNtp: true)
            : new(false, MensajeSinConexion);

        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            return SinHoraNtp();

        DateTimeOffset horaReal;
        try
        {
            horaReal = await relojNtp.ObtenerHoraUtcAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "No se pudo obtener la hora de internet para validar la licencia.");
            return SinHoraNtp();
        }

        var usuario = auth.CurrentUser;
        var estado = await database.ObtenerConfiguracionLicenciaSinActualizarAsync(usuarioId);
        var evaluacion = await licenciaPrueba.EvaluarAsync(estado, usuarioId, horaReal, usuario?.Correo);
        database.LimpiarLicenciaRemota();
        string aviso = string.Empty;
        try
        {
            using var limiteRemoto = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limiteRemoto.CancelAfter(TimeSpan.FromSeconds(8));
            var token = await auth.ObtenerIdTokenActualizadoAsync(limiteRemoto.Token);
            var remoto = await licenciaRemota.VerificarAsync(token, limiteRemoto.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(usuario, auth.CurrentUser))
                throw new AuthException("La sesión cambió durante la verificación de licencia.");
            if (remoto.Estado == "Activo" && remoto.Vencimiento is DateTimeOffset vencimiento && vencimiento > horaReal)
            {
                database.EstablecerLicenciaRemota(usuarioId, vencimiento.UtcDateTime,
                    evaluacion.Estado != EstadoAccesoLicencia.Permitido, () => ReferenceEquals(usuario, auth.CurrentUser));
                if (evaluacion.Estado != EstadoAccesoLicencia.Permitido)
                    return new(true, string.Empty, VenceUtc: vencimiento.UtcDateTime);
            }
            logger.LogInformation("Verificación remota de licencia completada: {Estado}.", remoto.Estado);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("La verificación remota de licencia excedió el tiempo límite.");
            aviso = "No se pudo consultar la licencia remota a tiempo; se mantiene la validación local.";
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.LogWarning("No se pudo consultar la licencia remota: {Tipo}.", ex.GetType().Name);
            aviso = "No se pudo consultar la licencia remota; se mantiene la validación local.";
        }
        if (!ReferenceEquals(usuario, auth.CurrentUser))
            throw new InvalidOperationException("La sesión cambió durante la validación de licencia.");
        return evaluacion.Estado switch
        {
            EstadoAccesoLicencia.Permitido => new(true, aviso, VenceUtc: evaluacion.VenceUtc),
            EstadoAccesoLicencia.RequiereActivacionPrueba => new(false, aviso, RequiereActivacionPrueba: true),
            EstadoAccesoLicencia.PruebaVencida => Bloquear(LicenciaPruebaService.MensajePruebaFinalizada),
            EstadoAccesoLicencia.LicenciaPagadaVencida => Bloquear(MensajeLicenciaVencida),
            EstadoAccesoLicencia.DispositivoNoAutorizado => Bloquear(LicenciaPruebaService.MensajeDispositivoNoAutorizado),
            EstadoAccesoLicencia.PruebaYaUsada => Bloquear(LicenciaPruebaService.MensajePruebaYaUsada),
            _ => throw new InvalidOperationException("El estado de licencia no es compatible.")
        };

        ResultadoValidacionLicenciaInicial Bloquear(string mensaje)
        {
            logger.LogInformation("Se bloqueó el inicio de {UsuarioId}: {Mensaje}", usuarioId, mensaje);
            return new(false, string.IsNullOrEmpty(aviso) ? mensaje : $"{mensaje}\n{aviso}");
        }
    }
}
