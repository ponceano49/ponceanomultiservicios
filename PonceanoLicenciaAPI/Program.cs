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
if (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Storage:EnableInMemory"))
    builder.Services.AddSingleton<ILicenciaRepository, LicenciaRepositoryMemoria>();
else
{
    builder.Services.AddScoped<PostgresLicenciaRepository>();
    builder.Services.AddScoped<ILicenciaRepository>(services => services.GetRequiredService<PostgresLicenciaRepository>());
    builder.Services.AddScoped<IPayPalPaymentStore>(services => services.GetRequiredService<PostgresLicenciaRepository>());
}
builder.Services.AddFirebaseIdTokenAuthentication(builder.Configuration);
builder.Services.AddHttpClient<PayPalVerifier>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<PayPalPaymentsClient>(client => client.Timeout = TimeSpan.FromSeconds(20))
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

app.MapPost("/api/pago/orden", async (HttpContext context, SolicitudOrdenPago solicitud, PayPalPaymentsClient paypal, CancellationToken cancellationToken) =>
{
    if (!FirebaseAuthentication.TryGetVerifiedEmail(context.User, out var correo))
        return Results.Forbid();
    if (!PlanPago.TryObtener(solicitud.Plan, out var plan))
        return Results.BadRequest(new { error = "Seleccione el plan mensual o anual." });
    var store = context.RequestServices.GetService<IPayPalPaymentStore>();
    if (store is null)
        return Results.Problem("El almacenamiento PostgreSQL de pagos no está disponible.", statusCode: 503);
    try
    {
        var referencia = Guid.NewGuid().ToString("N");
        var orden = await paypal.CrearOrdenAsync(referencia, plan, cancellationToken);
        await store.RegistrarOrdenAsync(orden.Id, referencia, correo, plan, cancellationToken);
        return Results.Ok(new { ordenId = orden.Id, plan = plan.Codigo, importe = plan.ImporteUsd, moneda = "USD", urlAprobacion = orden.UrlAprobacion });
    }
    catch (PayPalUnavailableException)
    {
        return Results.Problem("No se pudo iniciar el pago en PayPal Sandbox.", statusCode: 503);
    }
}).RequireAuthorization().RequireRateLimiting("licencia");

app.MapGet("/api/pago/retorno", async (string? token, HttpContext context, PayPalPaymentsClient paypal, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var store = context.RequestServices.GetService<IPayPalPaymentStore>();
    if (string.IsNullOrWhiteSpace(token) || store is null)
        return Results.Content("<html><body><h1>No se pudo verificar el pago</h1><p>Vuelve a PonceanoFacturador y contacta al administrador.</p></body></html>", "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
    try
    {
        var ordenExistente = await store.BuscarOrdenAsync(token, cancellationToken);
        if (ordenExistente is null)
            return Results.Content("<html><body><h1>Orden no reconocida</h1><p>Contacta al administrador.</p></body></html>", "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
        var pago = await paypal.CapturarYVerificarOrdenAsync(token, null, capturar: true, cancellationToken);
        if (pago is null)
            return Results.Content("<html><body><h1>Pago aún no confirmado</h1><p>Vuelve a la aplicación. La licencia se activa únicamente cuando PayPal confirma el cobro.</p></body></html>", "text/html; charset=utf-8", statusCode: StatusCodes.Status202Accepted);
        var resultado = await store.ProcesarPagoAsync(pago, cancellationToken);
        if (resultado is ResultadoProcesamientoPago.Activado or ResultadoProcesamientoPago.YaProcesado)
            return Results.Content("<html><body><h1>Pago confirmado</h1><p>La licencia quedó activada. Puedes volver a PonceanoFacturador.</p></body></html>", "text/html; charset=utf-8");
        logger.LogError("PayPal retornó una captura que no coincide con una orden registrada. Resultado={Resultado}; Orden={Orden}; Captura={Captura}", resultado, pago.IdOrden, pago.IdCaptura);
        return Results.Content("<html><body><h1>Pago recibido para revisión</h1><p>No se activó automáticamente. Contacta al administrador con tu comprobante.</p></body></html>", "text/html; charset=utf-8", statusCode: StatusCodes.Status202Accepted);
    }
    catch (PayPalUnavailableException)
    {
        return Results.Content("<html><body><h1>Verificación temporalmente no disponible</h1><p>Vuelve a la aplicación y consulta tu licencia más tarde.</p></body></html>", "text/html; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("webhook");

app.MapGet("/api/pago/cancelado", () => Results.Content("<html><body><h1>Pago cancelado</h1><p>No se realizó ningún cambio en la licencia. Puedes volver a PonceanoFacturador.</p></body></html>", "text/html; charset=utf-8"));

app.MapPost("/api/pago/webhook", async (HttpRequest request, PayPalVerifier verifier, PayPalPaymentsClient paypal, IServiceProvider services, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (!request.HasJsonContentType())
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    JsonDocument document;
    try { document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken); }
    catch (JsonException) { return Results.BadRequest(new { error = "El evento JSON no es válido." }); }
    using (document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
            !root.TryGetProperty("event_type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
            return Results.BadRequest(new { error = "El evento debe incluir id y event_type." });
        var result = await verifier.VerificarAsync(request.Headers, root, cancellationToken);
        if (result == ResultadoFirma.Invalida) return Results.Unauthorized();
        if (result == ResultadoFirma.NoDisponible)
            return Results.Problem("No se pudo verificar la firma de PayPal.", statusCode: 503);
        if (type.GetString() != "PAYMENT.CAPTURE.COMPLETED")
            return Results.Ok(new { estado = "Evento autenticado no aplicable." });
        if (!root.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object ||
            resource.GetPropertyOrDefault("status") != "COMPLETED" ||
            resource.GetPropertyOrDefault("id") is not { Length: > 0 } captureId ||
            !resource.TryGetProperty("supplementary_data", out var supplementary) || supplementary.ValueKind != JsonValueKind.Object ||
            !supplementary.TryGetProperty("related_ids", out var related) || related.ValueKind != JsonValueKind.Object ||
            related.GetPropertyOrDefault("order_id") is not { Length: > 0 } orderId ||
            !resource.TryGetProperty("amount", out var amount) ||
            !decimal.TryParse(amount.GetPropertyOrDefault("value"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var eventAmount) ||
            amount.GetPropertyOrDefault("currency_code") != "USD")
            return Results.BadRequest(new { error = "El evento de captura no tiene los datos esperados." });
        var store = services.GetService<IPayPalPaymentStore>();
        if (store is null)
            return Results.Problem("El almacenamiento PostgreSQL de pagos no está disponible.", statusCode: 503);
        try
        {
            var pago = await paypal.CapturarYVerificarOrdenAsync(orderId, captureId, capturar: false, cancellationToken);
            if (pago is null || pago.ImporteUsd != eventAmount || pago.Moneda != "USD")
            {
                logger.LogError("La captura de PayPal no coincide con la orden completada. Orden={Orden}; Captura={Captura}", orderId, captureId);
                return Results.Ok(new { estado = "Pago recibido para revisión; no se activó la licencia." });
            }
            pago = pago with { IdEvento = id.GetString() };
            var procesado = await store.ProcesarPagoAsync(pago, cancellationToken);
            if (procesado is ResultadoProcesamientoPago.OrdenDesconocida or ResultadoProcesamientoPago.DatosNoCoinciden)
                logger.LogError("Evento PayPal autenticado no conciliado. Resultado={Resultado}; Orden={Orden}; Captura={Captura}", procesado, orderId, captureId);
            return Results.Ok(new { estado = procesado.ToString() });
        }
        catch (PayPalUnavailableException)
        {
            return Results.Problem("No se pudo consultar la captura en PayPal.", statusCode: 503);
        }
    }
}).RequireRateLimiting("webhook");

app.Run();
