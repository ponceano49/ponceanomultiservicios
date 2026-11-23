using System.Runtime.CompilerServices;
#if WINDOWS
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;
#endif

namespace PonceanoFacturador.Services;

public interface IPreprocesadorFacturaOcr
{
    IAsyncEnumerable<byte[]> PrepararPaginasAsync(string ruta, CancellationToken cancellationToken = default);
}

public sealed class PreprocesadorFacturaOcr : IPreprocesadorFacturaOcr
{
    private readonly uint? _maximaDimension;

    public PreprocesadorFacturaOcr(uint? maximaDimension = null)
    {
        if (maximaDimension == 0) throw new ArgumentOutOfRangeException(nameof(maximaDimension));
        _maximaDimension = maximaDimension;
    }

#if WINDOWS
    private const double DpiObjetivo = 300;
    private const long MaximoPixeles = 40_000_000;
    private const uint MaximoBytesImagen = 950 * 1024;

    public async IAsyncEnumerable<byte[]> PrepararPaginasAsync(string ruta, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var archivo = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(ruta)).AsTask(cancellationToken);
        if (Path.GetExtension(ruta).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var documento = await PdfDocument.LoadFromFileAsync(archivo).AsTask(cancellationToken);
            if (documento.PageCount == 0) throw new InvalidDataException("El PDF no contiene páginas.");
            for (uint indice = 0; indice < documento.PageCount; indice++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var pagina = documento.GetPage(indice);
                var (ancho, alto) = Dimensiones(pagina.Size.Width * DpiObjetivo / 96, pagina.Size.Height * DpiObjetivo / 96);
                using var render = new InMemoryRandomAccessStream();
                await pagina.RenderToStreamAsync(render, new PdfPageRenderOptions { DestinationWidth = ancho, DestinationHeight = alto }).AsTask(cancellationToken);
                render.Seek(0);
                yield return await ConvertirAsync(render, false, cancellationToken);
            }
        }
        else
        {
            using var entrada = await archivo.OpenReadAsync().AsTask(cancellationToken);
            yield return await ConvertirAsync(entrada, true, cancellationToken);
        }
    }

    private async Task<byte[]> ConvertirAsync(IRandomAccessStream entrada, bool ajustarDpi, CancellationToken cancellationToken)
    {
        var decoder = await BitmapDecoder.CreateAsync(entrada).AsTask(cancellationToken);
        var dpiX = double.IsFinite(decoder.DpiX) && decoder.DpiX > 0 ? decoder.DpiX : 96;
        var dpiY = double.IsFinite(decoder.DpiY) && decoder.DpiY > 0 ? decoder.DpiY : 96;
        var (ancho, alto) = ajustarDpi
            ? Dimensiones(decoder.PixelWidth * DpiObjetivo / dpiX, decoder.PixelHeight * DpiObjetivo / dpiY)
            : Dimensiones(decoder.PixelWidth, decoder.PixelHeight);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transformacion = new BitmapTransform { ScaledWidth = ancho, ScaledHeight = alto, InterpolationMode = BitmapInterpolationMode.Cubic };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                transformacion, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
            var buffer = new Windows.Storage.Streams.Buffer(checked((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4)));
            bitmap.CopyToBuffer(buffer);
            CryptographicBuffer.CopyToByteArray(buffer, out var pixeles);
            for (var i = 0; i < pixeles.Length; i += 4)
            {
                if ((i & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                var alfa = pixeles[i + 3] / 255.0;
                for (var canal = 0; canal < 3; canal++)
                {
                    var valor = pixeles[i + canal] * alfa + 255 * (1 - alfa);
                    pixeles[i + canal] = (byte)Math.Clamp((int)Math.Round((valor - 128) * 1.25 + 128), 0, 255);
                }
                pixeles[i + 3] = 255;
            }
            bitmap.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(pixeles));
            bitmap.DpiX = DpiObjetivo;
            bitmap.DpiY = DpiObjetivo;
            var datos = await CodificarJpegAsync(bitmap, cancellationToken);
            if (datos.Length <= MaximoBytesImagen) return datos;

            var factor = Math.Min(0.9, Math.Sqrt((double)MaximoBytesImagen / datos.Length) * 0.9);
            var nuevoAncho = Math.Max(1, (uint)Math.Floor(ancho * factor));
            var nuevoAlto = Math.Max(1, (uint)Math.Floor(alto * factor));
            if (nuevoAncho == ancho && nuevoAlto == alto)
                throw new InvalidDataException("No fue posible comprimir la página por debajo del límite de 1 MB.");
            (ancho, alto) = Dimensiones(nuevoAncho, nuevoAlto);
        }
    }

    private static async Task<byte[]> CodificarJpegAsync(SoftwareBitmap bitmap, CancellationToken cancellationToken)
    {
        using var salida = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, salida).AsTask(cancellationToken);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken);
        if (salida.Size > int.MaxValue) throw new InvalidDataException("La página JPEG es demasiado grande para procesarla.");
        salida.Seek(0);
        using var lector = new DataReader(salida.GetInputStreamAt(0));
        var longitud = checked((uint)salida.Size);
        if (await lector.LoadAsync(longitud).AsTask(cancellationToken) != longitud)
            throw new InvalidDataException("No se pudo leer la página JPEG completa.");
        var datos = new byte[longitud];
        lector.ReadBytes(datos);
        return datos;
    }

    private (uint Ancho, uint Alto) Dimensiones(double ancho, double alto)
    {
        if (!double.IsFinite(ancho) || !double.IsFinite(alto) || ancho < 1 || alto < 1 || ancho > uint.MaxValue || alto > uint.MaxValue)
            throw new InvalidDataException("La página no tiene dimensiones válidas; revísala antes de procesarla.");
        if (_maximaDimension is uint maxima && Math.Max(ancho, alto) > maxima)
        {
            var escala = maxima / Math.Max(ancho, alto);
            ancho *= escala;
            alto *= escala;
        }
        ancho = Math.Max(1, Math.Round(ancho, MidpointRounding.AwayFromZero));
        alto = Math.Max(1, Math.Round(alto, MidpointRounding.AwayFromZero));
        if (ancho * alto > MaximoPixeles)
            throw new InvalidDataException("La página supera el límite de resolución; revísala antes de procesarla.");
        return (checked((uint)ancho), checked((uint)alto));
    }
#else
    public async IAsyncEnumerable<byte[]> PrepararPaginasAsync(string ruta, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("El preprocesamiento de facturas está disponible únicamente en Windows.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
#endif
}
