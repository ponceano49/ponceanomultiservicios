using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

var db = new DatabaseService();
var firebase = new FirebaseHandler();
var api = new ApiHandler();
var auth = new AuthService(db, new HttpClient(firebase));
var remote = new LicenciaRemotaService(new HttpClient(api));
var clock = new FakeClock();
var trial = new LicenciaPruebaService(db, new IdentificadorDispositivoService(), NullLogger<LicenciaPruebaService>.Instance);
var validation = new ValidacionLicenciaNtpService(auth, db, clock, trial, NullLogger<ValidacionLicenciaNtpService>.Instance, remote);
await auth.SignInAsync("test@example.test", "test", true);
var localExpiry = DateTime.UtcNow.AddDays(10);
db.State = new ConfiguracionUsuario { LicenciaTipo = "Mensual", LicenciaVencimiento = localExpiry };
api.Body = "{\"estadoLicencia\":\"Pendiente\",\"fechaVencimiento\":null}";
var result = await validation.ValidarAntesDeEntrarAsync();
Check(result.Permitida && db.Remota is null && db.State.LicenciaVencimiento == localExpiry, "pendiente conserva licencia local");
Check(firebase.RefreshCount == 1 && api.Token == "renewed-token" && api.Uri == LicenciaRemotaService.Direccion, "token renovado enviado al endpoint fijo");
firebase.RefreshUserId = "other-uid";
result = await validation.ValidarAntesDeEntrarAsync();
Check(result.Permitida && result.Mensaje.Contains("remota") && db.Remota is null, "token de otra identidad no autoriza licencia remota");
firebase.RefreshUserId = "uid";
api.Body = "{\"estadoLicencia\":\"Vencido\",\"fechaVencimiento\":\"2020-01-01T00:00:00Z\"}";
Check((await validation.ValidarAntesDeEntrarAsync()).Permitida && db.Remota is null, "vencida remota no revoca local");
api.Status = HttpStatusCode.ServiceUnavailable;
result = await validation.ValidarAntesDeEntrarAsync();
Check(result.Permitida && result.Mensaje.Contains("remota") && db.State.LicenciaVencimiento == localExpiry, "fallo remoto informa y conserva local");
api.Status = HttpStatusCode.Found;
Check((await validation.ValidarAntesDeEntrarAsync()).Permitida && db.Remota is null, "redirección rechazada");
api.Status = HttpStatusCode.OK;
api.Body = "{\"estadoLicencia\":\"Activo\",\"fechaVencimiento\":null}";
Check((await validation.ValidarAntesDeEntrarAsync()).Mensaje.Contains("remota"), "respuesta activa inválida informa");
var remoteExpiry = DateTimeOffset.UtcNow.AddDays(20);
api.Body = $"{{\"estadoLicencia\":\"Activo\",\"fechaVencimiento\":\"{remoteExpiry:O}\"}}";
result = await validation.ValidarAntesDeEntrarAsync();
Check(result.Permitida && db.Remota is { Priorizar: false } && db.State.LicenciaVencimiento == localExpiry, "local vigente tiene prioridad");
db.State = new ConfiguracionUsuario { LicenciaTipo = "Mensual", LicenciaVencimiento = DateTime.UtcNow.AddDays(-1) };
result = await validation.ValidarAntesDeEntrarAsync();
Check(result.Permitida && db.Remota is { Priorizar: true } && db.State.LicenciaVencimiento < DateTime.UtcNow, "remota activa permite acceso sin sobrescribir local vencida");
api.Body = "{\"estadoLicencia\":\"Pendiente\",\"fechaVencimiento\":null}";
Check(!(await validation.ValidarAntesDeEntrarAsync()).Permitida && db.Remota is null, "pendiente elimina estado remoto previo");
api.Status = HttpStatusCode.Unauthorized;
result = await validation.ValidarAntesDeEntrarAsync();
Check(!result.Permitida && result.Mensaje.Contains("remota"), "fallo remoto se comunica incluso al bloquear");
api.Status = HttpStatusCode.OK;
db.State = new ConfiguracionUsuario();
api.Body = "{\"estadoLicencia\":\"Pendiente\",\"fechaVencimiento\":null}";
result = await validation.ValidarAntesDeEntrarAsync();
Check(result.RequiereActivacionPrueba && db.TrialWrites == 0, "nueva cuenta conserva activación de prueba");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    try { await validation.ValidarAntesDeEntrarAsync(cancel.Token); throw new Exception("cancelación ignorada"); }
    catch (OperationCanceledException) { }
}
api.Body = $"{{\"estadoLicencia\":\"Activo\",\"fechaVencimiento\":\"{remoteExpiry:O}\"}}";
await validation.ValidarAntesDeEntrarAsync();
auth.SignOut();
Check(db.Remota is null && auth.CurrentUser is null, "cerrar sesión borra estado remoto");
try { await auth.ObtenerIdTokenActualizadoAsync(); throw new Exception("token obtenido sin sesión"); }
catch (AuthException) { }
await auth.SignInAsync("test@example.test", "test", true);
var restored = new AuthService(db, new HttpClient(firebase));
Check(await restored.RestoreAsync() && await restored.ObtenerIdTokenActualizadoAsync() == "renewed-token", "sesión recordada renueva el token");
restored.SignOut();
using (var retry = new SemaphoreSlim(1, 1))
{
    await restored.WaitForVerificationAsync(new PendingRegistration("initial-token", "refresh"), CancellationToken.None, retry);
    Check(await restored.ObtenerIdTokenActualizadoAsync() == "renewed-token", "registro verificado renueva sus claims");
}
restored.SignOut();
firebase.Email = AuthService.CorreoCuentaMaestra;
await auth.SignInAsync(firebase.Email, "test", true);
Connectivity.Current.NetworkAccess = NetworkAccess.None;
result = await validation.ValidarAntesDeEntrarAsync();
Check(result.Permitida && result.AdministradorSinNtp, "administrador offline conserva acceso");
Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
auth.SignOut();
Console.WriteLine("LicenciaRemotaChecks: pruebas superadas.");

static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
sealed class FakeClock : IRelojNtpService
{
    public Task<DateTimeOffset> ObtenerHoraUtcAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DateTimeOffset.UtcNow);
    }
}
sealed class FirebaseHandler : HttpMessageHandler
{
    public int RefreshCount { get; private set; }
    public string RefreshUserId { get; set; } = "uid";
    public string Email { get; set; } = "test@example.test";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        string body;
        if (path.EndsWith(":signInWithPassword")) body = "{\"idToken\":\"initial-token\",\"refreshToken\":\"refresh\"}";
        else if (path.EndsWith(":lookup")) body = $"{{\"users\":[{{\"localId\":\"uid\",\"email\":\"{Email}\",\"emailVerified\":true}}]}}";
        else if (path.EndsWith("/token")) { RefreshCount++; body = $"{{\"id_token\":\"renewed-token\",\"refresh_token\":\"refresh\",\"user_id\":\"{RefreshUserId}\"}}"; }
        else throw new Exception("endpoint Firebase inesperado");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
sealed class ApiHandler : HttpMessageHandler
{
    public string Body { get; set; } = "{}";
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string? Token { get; private set; }
    public string? Uri { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Uri = request.RequestUri?.AbsoluteUri;
        CheckHeader(request);
        Token = request.Headers.Authorization?.Parameter;
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
    private static void CheckHeader(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get || request.Headers.Authorization?.Scheme != "Bearer")
            throw new Exception("solicitud de licencia incorrecta");
    }
}
