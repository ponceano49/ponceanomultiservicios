namespace PonceanoFacturador.Views;

public interface IDocumentoEditable
{
    bool HayCambios { get; }
    bool EstaOcupado { get; }
    string? ErrorGuardado { get; }
    Task<bool> GuardarAntesDeSalirAsync();
}

public static class DocumentoNavegacion
{
    public static async Task<bool> ConfirmarSalidaAsync(Page dialogo, IDocumentoEditable documento)
    {
        if (documento.EstaOcupado)
        {
            await dialogo.DisplayAlertAsync("Documento", "Espera a que termine la operación antes de salir.", "Aceptar");
            return false;
        }
        if (!documento.HayCambios) return true;
        var respuesta = await dialogo.DisplayActionSheetAsync("¿Guardar los cambios antes de salir?", "CANCELAR", null, "GUARDAR", "DESCARTAR");
        if (respuesta == "DESCARTAR") return true;
        if (respuesta != "GUARDAR") return false;
        if (await documento.GuardarAntesDeSalirAsync()) return true;
        await dialogo.DisplayAlertAsync("Documento sin guardar", string.IsNullOrWhiteSpace(documento.ErrorGuardado) ? "No se pudo guardar. Revisa los datos del documento antes de salir." : documento.ErrorGuardado, "Aceptar");
        return false;
    }
}
