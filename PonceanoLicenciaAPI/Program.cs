using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Npgsql;
using PonceanoLicenciaAPI;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration["PORT"];
if (!string.IsNullOrWhiteSpace(port))
{
    if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var portNumber) || portNumber is < 1 or > 65535)
        throw new InvalidOperationException("PORT debe ser un puerto TCP válido entre 1 y 65535.");
    builder.WebHost.UseUrls($"http://0.0.0.0:{portNumber}");
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = null);
if (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Storage:EnableInMemory"))
    builder.Services.AddSingleton<ILicenciaRepository, LicenciaRepositoryMemoria>();
else
    builder.Services.AddScoped<ILicenciaRepository, PostgresLicenciaRepository>();
builder.Services.AddFirebaseIdTokenAuthentication(builder.Configuration);
builder.Services.AddHttpClient<PayPalVerifier>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("licencia", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("webhook", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var etapaInicio = "resolver el repositorio de licencias";
try
{
    await using var startupScope = app.Services.CreateAsyncScope();
    if (startupScope.ServiceProvider.GetRequiredService<ILicenciaRepository>() is PostgresLicenciaRepository postgres)
    {
        etapaInicio = "conectar PostgreSQL y preparar el esquema";
        await postgres.EnsureCreatedAsync(CancellationToken.None);
    }
}
catch (Exception ex)
{
    app.Logger.LogCritical("No se pudo {Etapa}; la API no arrancará. Revise la configuración y conectividad de PostgreSQL. No se registran mensajes de excepción porque pueden contener secretos.", etapaInicio);
    app.Logger.LogError("Configuración no vacía: ConnectionStrings__Licencias={CadenaConfigurada}; DATABASE_URL={UrlConfigurada}.",
        !string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Licencias")),
        !string.IsNullOrWhiteSpace(app.Configuration["DATABASE_URL"]));
    for (Exception? actual = ex; actual is not null; actual = actual.InnerException)
    {
        app.Logger.LogError("Tipo de excepción: {Tipo}; SQLSTATE: {SqlState}; error de socket: {SocketError}",
            actual.GetType().FullName,
            (actual as PostgresException)?.SqlState,
            (actual as System.Net.Sockets.SocketException)?.SocketErrorCode.ToString());
    }
    await app.DisposeAsync();
    Environment.ExitCode = 1;
    return;
}
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    await next();
});

app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.MapGet("/api/licencia/verificar", async (string? correo, HttpContext context, ILicenciaRepository repository, CancellationToken cancellationToken) =>
{
    if (!FirebaseAuthentication.TryGetVerifiedEmail(context.User, out var identidad))
        return Results.Forbid();
    if (correo is not null && (!Correo.TryNormalizar(correo, out var solicitado) ||
        !string.Equals(solicitado, identidad, StringComparison.Ordinal)))
        return Results.Forbid();
    var licencia = await repository.BuscarAsync(identidad, cancellationToken);
    var estado = licencia is null ? "Pendiente" : licencia.FechaVencimiento <= DateTimeOffset.UtcNow ? "Vencido" : licencia.EstadoLicencia;
    return Results.Ok(new { EstadoLicencia = estado, FechaVencimiento = licencia?.FechaVencimiento });
}).RequireAuthorization().RequireRateLimiting("licencia");

app.MapPost("/api/licencia/activar-manual", async (HttpContext context, ActivacionManual solicitud, ILicenciaRepository repository, CancellationToken cancellationToken) =>
{
    var adminEmail = app.Configuration["AdminEmail"];
    var key = app.Configuration["AdminKey"];
    if (string.IsNullOrWhiteSpace(adminEmail) || !Correo.TryNormalizar(adminEmail, out var adminNormalizado) ||
        string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
        return Results.Problem("La autorización administrativa no está configurada de forma segura.", statusCode: 503);
    if (!FirebaseAuthentication.TryGetVerifiedEmail(context.User, out var identidad) ||
        !string.Equals(identidad, adminNormalizado, StringComparison.Ordinal))
        return Results.Forbid();
    var header = context.Request.Headers["X-Admin-Key"];
    if (header.Count != 1 || string.IsNullOrEmpty(header[0]) || header[0]!.Length > 1024 ||
        !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(header[0]!))))
        return Results.Unauthorized();
    if (!Correo.TryNormalizar(solicitud.CorreoUsuario, out var correo) || solicitud.FechaVencimiento is null ||
        !Regex.IsMatch(solicitud.FechaVencimiento, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) ||
        !DateTimeOffset.TryParse(solicitud.FechaVencimiento, CultureInfo.InvariantCulture, DateTimeStyles.None, out var vencimiento) ||
        vencimiento <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(solicitud.CodigoTransaccionPago) ||
        solicitud.CodigoTransaccionPago.Trim().Length > 128)
        return Results.BadRequest(new { error = "Indique correo válido, vencimiento futuro con zona horaria y código de transacción de 1 a 128 caracteres." });
    try
    {
        var licencia = await repository.ActivarAsync(correo, vencimiento, solicitud.CodigoTransaccionPago.Trim(), cancellationToken);
        return Results.Ok(new { licencia.Id, licencia.CorreoUsuario, licencia.EstadoLicencia, licencia.FechaVencimiento, licencia.CodigoTransaccionPago });
    }
    catch (TransaccionDuplicadaException)
    {
        return Results.Conflict(new { error = "El código de transacción ya se utilizó con otros datos." });
    }
}).RequireAuthorization().RequireRateLimiting("licencia");

app.MapPost("/api/pago/webhook", async (HttpRequest request, PayPalVerifier verifier, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (!request.HasJsonContentType())
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    JsonDocument document;
    try { document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken); }
    catch (JsonException) { return Results.BadRequest(new { error = "El evento JSON no es válido." }); }
    using (document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
            !document.RootElement.TryGetProperty("event_type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
            return Results.BadRequest(new { error = "El evento debe incluir id y event_type." });
        var result = await verifier.VerificarAsync(request.Headers, document.RootElement, cancellationToken);
        if (result == ResultadoFirma.Invalida) return Results.Unauthorized();
        if (result == ResultadoFirma.NoDisponible)
            return Results.Problem("No se pudo verificar la firma de PayPal.", statusCode: 503);
        logger.LogWarning("Evento PayPal verificado; el procesamiento de pagos aún no está configurado.");
        // No confirmar el evento hasta implementar persistencia, idempotencia y conciliación del pago.
        return Results.Problem("Firma verificada, pero el procesamiento de pagos aún no está configurado.", statusCode: 503);
    }
}).RequireRateLimiting("webhook");

app.Run();
