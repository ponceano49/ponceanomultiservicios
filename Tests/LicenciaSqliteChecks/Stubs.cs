using PonceanoFacturador.Models;

public static class FileSystem { public static string AppDataDirectory => Path.GetTempPath(); }
public sealed class Color { public static Color FromArgb(string value) => new(); }
namespace PonceanoFacturador.Services
{
    public sealed record ProblemaRegistroFiscal(RegistroFiscal Registro, IReadOnlyList<string> Errores);
    public static class LicenciaPruebaService
    {
        public const int DuracionPruebaDias = 30;
        public const string TipoPrueba = "Prueba gratuita 30 días";
    }
    public static class FormatoEnvioDgiiService
    {
        public static IReadOnlyList<string> ValidarRegistro(RegistroFiscal registro, string tipo, DateTime inicio) => Array.Empty<string>();
    }
}
