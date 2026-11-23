using System.Net.Http;

namespace PonceanoFacturador.Services;

public sealed class DescargaActualizacionService(HttpClient http)
{
    private const long MaximoBytes = 1024L * 1024 * 1024;

    public async Task<string> DescargarAsync(Uri enlace, string directorio, bool esWindows, CancellationToken cancellationToken)
    {
        if (!ActualizacionService.EsEnlaceDescargaPermitido(enlace))
            throw new InvalidDataException("El servidor de descarga no está autorizado.");
        Directory.CreateDirectory(directorio);
        var nombre = $"PonceanoFacturador_Actualizacion_{Guid.NewGuid():N}{(esWindows ? ".zip" : ".apk")}";
        var ruta = Path.Combine(directorio, nombre);
        var temporal = ruta + ".part";
        try
        {
            using var response = await http.GetAsync(enlace, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var final = response.RequestMessage?.RequestUri ?? enlace;
            if (!ActualizacionService.EsEnlaceDescargaPermitido(final))
                throw new InvalidDataException("La descarga redirigió a un servidor no autorizado.");
            if (response.Content.Headers.ContentLength is long longitud && (longitud < 4 || longitud > MaximoBytes))
                throw new InvalidDataException("El tamaño del archivo de actualización no es válido.");
            await using var entrada = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var salida = new FileStream(temporal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                var firma = new byte[4];
                int leidos;
                while ((leidos = await entrada.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    var copiarFirma = (int)Math.Min(leidos, Math.Max(0, 4 - total));
                    if (copiarFirma > 0) Array.Copy(buffer, 0, firma, (int)total, copiarFirma);
                    total += leidos;
                    if (total > MaximoBytes) throw new InvalidDataException("El archivo de actualización excede el tamaño permitido.");
                    await salida.WriteAsync(buffer.AsMemory(0, leidos), cancellationToken);
                }
                if (total < 4 || firma[0] != 0x50 || firma[1] != 0x4b || firma[2] != 0x03 || firma[3] != 0x04)
                    throw new InvalidDataException("La respuesta no es un archivo ZIP o APK válido; Google Drive podría requerir confirmación.");
                if (response.Content.Headers.ContentLength is long esperado && total != esperado)
                    throw new InvalidDataException("La descarga de actualización está incompleta.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            using (var archivo = System.IO.Compression.ZipFile.OpenRead(temporal))
            {
                if (archivo.Entries.Count == 0)
                    throw new InvalidDataException("La actualización descargada está vacía.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporal, ruta);
            return ruta;
        }
        finally
        {
            if (File.Exists(temporal)) File.Delete(temporal);
        }
    }
}
