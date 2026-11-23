namespace PonceanoFacturador.Services;

public static class ServidorLicenciaConfig
{
    private const string Clave = "licencia_servidor_url_pendiente_v1";

    public static string Obtener() => Preferences.Default.Get(Clave, string.Empty);

    public static void Guardar(string? valor)
    {
        var texto = valor?.Trim() ?? string.Empty;
        if (texto.Length == 0)
        {
            Preferences.Default.Remove(Clave);
            return;
        }

        if (texto.Length > 2048 || !Uri.TryCreate(texto, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.HostNameType != UriHostNameType.Dns ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Introduce solo el dominio HTTPS de la API, sin puerto, ruta, usuario ni parámetros.");

        Preferences.Default.Set(Clave, uri.GetLeftPart(UriPartial.Authority));
    }
}
