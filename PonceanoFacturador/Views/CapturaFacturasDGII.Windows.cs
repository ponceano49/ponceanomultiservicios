#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PonceanoFacturador.Views;

public partial class CapturaFacturasDGII
{
    private FrameworkElement? _dropTarget;

    private void ActivarArrastreArchivos()
    {
        if (_dropTarget is not null) return;
        var destino = Handler?.PlatformView as FrameworkElement;
        if (destino is null) return;
        _dropTarget = destino;
        _dropTarget.AllowDrop = true;
        _dropTarget.DragOver += OnArchivosDragOver;
        _dropTarget.Drop += OnArchivosDrop;
    }

    private void DesactivarArrastreArchivos()
    {
        if (_dropTarget is null) return;
        _dropTarget.DragOver -= OnArchivosDragOver;
        _dropTarget.Drop -= OnArchivosDrop;
        _dropTarget.AllowDrop = false;
        _dropTarget = null;
    }

    private static void OnArchivosDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void OnArchivosDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                throw new InvalidDataException("Arrastra archivos PDF, JPG, JPEG o PNG.");

            var items = await e.DataView.GetStorageItemsAsync();
            var archivos = items.OfType<StorageFile>()
                .Where(file => EsFormatoAdmitido(file.Name))
                .Select(file => new FacturaPendiente(file.Name, file.OpenStreamForReadAsync))
                .ToList();
            var noAdmitidos = items.Count - archivos.Count;
            if (noAdmitidos > 0)
                await DisplayAlertAsync("Formato no compatible", $"Se omitieron {noAdmitidos} elementos. Solo se aceptan JPG, JPEG, PNG y PDF.", "Aceptar");
            await AgregarArchivosAsync(archivos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron procesar los archivos arrastrados a la captura DGII.");
            await DisplayAlertAsync("Captura DGII", ex.Message, "Aceptar");
        }
    }
}
#endif
