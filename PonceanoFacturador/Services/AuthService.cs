using System.Net.Http.Json;
using System.Text.Json;
using PonceanoFacturador.Models;

namespace PonceanoFacturador.Services;

public class AuthException : Exception
{
    public string? FirebaseErrorCode { get; }

    public AuthException(string message, string? firebaseErrorCode = null) : base(message)
    {
        FirebaseErrorCode = firebaseErrorCode;
    }
}

public sealed class EmailNoVerificadoException : AuthException
{
    public EmailNoVerificadoException() : base("Confirma el enlace que te llegó a tu correo y presiona Reintentar.") { }
}

public sealed class ConexionAuthException : AuthException
{
    public ConexionAuthException() : base("Se requiere internet para iniciar sesión o registrarse.") { }
}

public sealed record PendingRegistration(string IdToken, string RefreshToken);

public sealed class AuthService
{
    public const string CorreoCuentaMaestra = "ponceanomultiservicios@hotmail.com";
    private const string SessionKey = "firebase_session_v1";
    private readonly HttpClient _http;
    private readonly DatabaseService _database;
    private readonly object _sessionLock = new();
    private string? _refreshToken;
    public UsuarioAutenticado? CurrentUser { get; private set; }
    public bool EsAdministrador => EsCorreoAdministrador(CurrentUser?.Correo);
    private sealed record StoredSession(string RefreshToken, string IdFirebase, string Nombre, string Correo);

    private static bool EsCorreoAdministrador(string? correo)
    {
        if (string.IsNullOrWhiteSpace(correo)) return false;
        if (string.Equals(correo.Trim(), CorreoCuentaMaestra, StringComparison.OrdinalIgnoreCase))
            return true;
#if DEBUG
        return correo.Trim().Equals("ponceano490@gmail.com", StringComparison.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    public static bool EsAccesoPruebaDebug(string? correo)
    {
#if DEBUG && !PONCEANO_DESACTIVAR_PRUEBAS
        if (string.IsNullOrWhiteSpace(correo)) return false;
        return correo.Trim().Equals("ponceanomultiservicios@hotmail.com", StringComparison.OrdinalIgnoreCase)
            || correo.Trim().Equals("ponceano490@gmail.com", StringComparison.OrdinalIgnoreCase)
            || correo.Trim().Equals("ponceanomultisevicios@gmail.com", StringComparison.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    public AuthService(DatabaseService database, HttpClient http)
    {
        _database = database;
        _http = http;
    }

    private static void RequireInternet()
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            throw new ConexionAuthException();
        if (string.IsNullOrWhiteSpace(FirebaseOptions.WebApiKey))
            throw new AuthException("Firebase no está configurado. Contacta al administrador.");
    }

    private async Task<JsonElement> PostAsync(string endpoint, object payload, CancellationToken cancellationToken = default)
    {
        RequireInternet();
        using var response = await _http.PostAsJsonAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:{endpoint}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}", payload, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!response.IsSuccessStatusCode)
        {
            var code = json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
                ? message.GetString() : null;
            throw new AuthException(code switch
            {
                "EMAIL_EXISTS" => "Este correo ya está registrado.",
                "INVALID_EMAIL" => "Introduce un correo electrónico válido.",
                "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "INVALID_LOGIN_CREDENTIALS" => "Correo o contraseña incorrectos.",
                "USER_DISABLED" => "Esta cuenta está deshabilitada.",
                "TOO_MANY_ATTEMPTS_TRY_LATER" => "Demasiados intentos. Vuelve a intentarlo más tarde.",
                "OPERATION_NOT_ALLOWED" => "El acceso por correo no está habilitado en Firebase.",
                _ when code?.StartsWith("WEAK_PASSWORD", StringComparison.Ordinal) == true => "La contraseña no cumple los requisitos de Firebase.",
                _ => "Firebase rechazó la solicitud. Comprueba la configuración e inténtalo de nuevo."
            }, code);
        }
        return json.RootElement.Clone();
    }

    private static string Required(JsonElement json, string name)
    {
        if (json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!;
        throw new AuthException("La respuesta de Firebase está incompleta.");
    }

    public async Task<PendingRegistration> RegisterAsync(string nombre, string correo, string password)
    {
        var result = await PostAsync("signUp", new { email = correo, password, returnSecureToken = true });
        var token = Required(result, "idToken");
        var refresh = Required(result, "refreshToken");
        try
        {
            var updated = await PostAsync("update", new { idToken = token, displayName = nombre, returnSecureToken = true });
            token = Required(updated, "idToken");
            refresh = Required(updated, "refreshToken");
            if (!EsAccesoPruebaDebug(correo))
                await PostAsync("sendOobCode", new { requestType = "VERIFY_EMAIL", idToken = token });
        }
        catch (Exception ex) when (ex is AuthException or HttpRequestException or TaskCanceledException)
        {
            throw new AuthException("La cuenta se creó, pero no se pudo completar su perfil o enviar la verificación. Inicia sesión para continuar.");
        }
        return new PendingRegistration(token, refresh);
    }

    public async Task WaitForVerificationAsync(PendingRegistration pending, CancellationToken cancellationToken, SemaphoreSlim? reintento = null)
    {
        while (true)
        {
            if (reintento is null)
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            else
                await reintento.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var user = await LookupAsync(pending.IdToken, cancellationToken);
            if (user.Verified)
            {
                await CompleteAsync(user, pending.RefreshToken, false, cancellationToken);
                return;
            }
        }
    }

    public async Task SignInAsync(string correo, string password, bool remember, CancellationToken cancellationToken = default)
    {
        var result = await PostAsync("signInWithPassword", new { email = correo, password, returnSecureToken = true }, cancellationToken);
        var token = Required(result, "idToken");
        var user = await LookupAsync(token, cancellationToken);
        if (!user.Verified && !EsAccesoPruebaDebug(user.Correo))
        {
            await PostAsync("sendOobCode", new { requestType = "VERIFY_EMAIL", idToken = token }, cancellationToken);
            throw new EmailNoVerificadoException();
        }
        await CompleteAsync(user, Required(result, "refreshToken"), remember, cancellationToken);
    }

    public async Task<bool> ValidarContraseñaMaestraAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var correoAdministrador = CorreoCuentaMaestra;
        JsonElement result;
        try
        {
            result = await PostAsync("signInWithPassword", new
            {
                email = correoAdministrador,
                password,
                returnSecureToken = true
            }, cancellationToken);
        }
        catch (AuthException ex) when (ex.FirebaseErrorCode is "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "INVALID_LOGIN_CREDENTIALS")
        {
            return false;
        }

        var user = await LookupAsync(Required(result, "idToken"), cancellationToken);
        if (!user.Verified)
            throw new AuthException("La cuenta de administrador debe tener el correo verificado.");
        return string.Equals(user.Correo, correoAdministrador, StringComparison.OrdinalIgnoreCase);
    }

    public async Task SendResetAsync(string correo)
    {
        await PostAsync("sendOobCode", new { requestType = "PASSWORD_RESET", email = correo });
    }

    private sealed record FirebaseUser(string Id, string Nombre, string Correo, bool Verified);
    private async Task<FirebaseUser> LookupAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await PostAsync("lookup", new { idToken = token }, cancellationToken);
        if (!result.TryGetProperty("users", out var users) || users.GetArrayLength() == 0)
            throw new AuthException("No se encontró la cuenta en Firebase.");
        var user = users[0];
        var email = Required(user, "email");
        var name = user.TryGetProperty("displayName", out var displayName) ? displayName.GetString() : null;
        return new FirebaseUser(Required(user, "localId"), string.IsNullOrWhiteSpace(name) ? email : name, email,
            user.TryGetProperty("emailVerified", out var verified) && verified.ValueKind == JsonValueKind.True);
    }

    private async Task CompleteAsync(FirebaseUser user, string refreshToken, bool remember, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = new UsuarioAutenticado { IdFirebase = user.Id, Nombre = user.Nombre, Correo = user.Correo, UltimoAcceso = DateTime.UtcNow };
        await _database.SaveUserAsync(account);
        cancellationToken.ThrowIfCancellationRequested();
        if (remember)
            await SecureStorage.Default.SetAsync(SessionKey, JsonSerializer.Serialize(new StoredSession(refreshToken, user.Id, user.Nombre, user.Correo)));
        else
            SecureStorage.Default.Remove(SessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sessionLock)
        {
            _database.LimpiarLicenciaRemota();
            _refreshToken = refreshToken;
            CurrentUser = account;
        }
    }

    public async Task<string> ObtenerIdTokenActualizadoAsync(CancellationToken cancellationToken = default)
    {
        RequireInternet();
        UsuarioAutenticado usuario;
        string refresh;
        lock (_sessionLock)
        {
            usuario = CurrentUser ?? throw new AuthException("Inicia sesión antes de verificar la licencia remota.");
            refresh = _refreshToken ?? throw new AuthException("La sesión no dispone de un token para verificar la licencia remota.");
        }
        using var response = await _http.PostAsync($"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AuthException("No se pudo renovar la sesión Firebase para verificar la licencia.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var token = Required(json.RootElement, "id_token");
        var nuevoRefresh = Required(json.RootElement, "refresh_token");
        lock (_sessionLock)
        {
            if (!ReferenceEquals(usuario, CurrentUser) || Required(json.RootElement, "user_id") != usuario.IdFirebase)
                throw new AuthException("La sesión cambió durante la verificación de licencia.");
            _refreshToken = nuevoRefresh;
        }
        return token;
    }

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var stored = await SecureStorage.Default.GetAsync(SessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(stored)) return false;
        var session = JsonSerializer.Deserialize<StoredSession>(stored) ?? throw new AuthException("No se pudo leer la sesión guardada.");
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sessionLock)
            {
                _database.LimpiarLicenciaRemota();
                _refreshToken = session.RefreshToken;
                CurrentUser = new UsuarioAutenticado { IdFirebase = session.IdFirebase, Nombre = session.Nombre, Correo = session.Correo };
            }
            return true;
        }
        RequireInternet();
        using var response = await _http.PostAsync($"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken }), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var code = error.RootElement.TryGetProperty("error", out var details) && details.TryGetProperty("message", out var message)
                ? message.GetString() : null;
            if (code is "TOKEN_EXPIRED" or "USER_DISABLED" or "USER_NOT_FOUND" or "INVALID_REFRESH_TOKEN")
            {
                SecureStorage.Default.Remove(SessionKey);
                throw new AuthException("La sesión guardada ha caducado o fue revocada. Inicia sesión de nuevo.");
            }
            throw new AuthException("No se pudo renovar la sesión. Comprueba la conexión y la configuración de Firebase.");
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var user = await LookupAsync(Required(json.RootElement, "id_token"), cancellationToken);
        if (!user.Verified && !EsAccesoPruebaDebug(user.Correo))
            throw new AuthException("Debes verificar tu correo antes de iniciar sesión.");
        await CompleteAsync(user, Required(json.RootElement, "refresh_token"), true, cancellationToken);
        return true;
    }

    public void SignOut()
    {
        SecureStorage.Default.Remove(SessionKey);
        lock (_sessionLock)
        {
            CurrentUser = null;
            _refreshToken = null;
            _database.LimpiarLicenciaRemota();
        }
    }

    public static string UserMessage(Exception ex) => ex switch
    {
        AuthException auth => auth.Message,
        HttpRequestException or TaskCanceledException => "No se pudo conectar con Firebase. Comprueba tu conexión e inténtalo de nuevo.",
        _ => "No se pudo completar la operación. Revisa la configuración y vuelve a intentarlo."
    };
}
