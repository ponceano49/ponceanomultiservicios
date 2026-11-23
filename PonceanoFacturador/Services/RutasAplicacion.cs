namespace PonceanoFacturador.Services;

public static class RutasAplicacion
{
    public static string DirectorioDgii
    {
        get
        {
#if WINDOWS
            var documentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documentos))
                throw new InvalidOperationException("No se encontró la carpeta Documentos del usuario.");
            return Path.Combine(documentos, "DGII Automática");
#else
            return Path.Combine(FileSystem.AppDataDirectory, "DGII Automática");
#endif
        }
    }
}
