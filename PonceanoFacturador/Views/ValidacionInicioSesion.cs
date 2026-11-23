using System.Net.Mail;

namespace PonceanoFacturador.Views;

internal static class ValidacionInicioSesion
{
    public static string? Mensaje(string? correo, string? contrasena)
    {
        if (string.IsNullOrWhiteSpace(correo) && string.IsNullOrEmpty(contrasena))
            return "Introduce tu correo electrónico y contraseña.";
        if (string.IsNullOrWhiteSpace(correo))
            return "Introduce tu correo electrónico.";
        if (string.IsNullOrEmpty(contrasena))
            return "Introduce tu contraseña.";
        if (!MailAddress.TryCreate(correo.Trim(), out _))
            return "Introduce un correo electrónico válido.";
        return null;
    }
}
