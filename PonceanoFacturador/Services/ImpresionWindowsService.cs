using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace PonceanoFacturador.Services;

public sealed class ImpresionWindowsService
{
    private const string PreferenciaImpresora = "impresora-seleccionada";
    private const uint ImpresorasLocalesYConectadas = 0x00000002 | 0x00000004;

    public string ImpresoraSeleccionada => Preferences.Default.Get(PreferenciaImpresora, string.Empty);

    public IReadOnlyList<string> ListarImpresorasInstaladas()
    {
#if WINDOWS
        return EnumerarImpresorasInstaladas();
#else
        throw new PlatformNotSupportedException("La lista de impresoras instaladas solo está disponible en Windows.");
#endif
    }

    public async Task AbrirVistaPreviaAsync(string rutaPdf, string titulo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rutaPdf);
        if (!File.Exists(rutaPdf)) throw new FileNotFoundException("No se encontró el PDF para imprimir.", rutaPdf);
        await PrepararImpresoraSeleccionadaAsync();
        if (!await Launcher.Default.OpenAsync(new OpenFileRequest(titulo, new ReadOnlyFile(rutaPdf))))
            throw new InvalidOperationException("No se pudo abrir la vista previa del documento en el visor del sistema.");
    }

    public async Task SeleccionarImpresoraAsync(Page pagina)
    {
        ArgumentNullException.ThrowIfNull(pagina);
#if WINDOWS
        var impresoras = ListarImpresorasInstaladas();
        if (impresoras.Count == 0) throw new InvalidOperationException("Windows no tiene impresoras instaladas o accesibles.");
        var seleccion = await pagina.DisplayActionSheetAsync("Seleccionar impresora predeterminada", "Cancelar", null, impresoras.ToArray());
        if (seleccion is null or "Cancelar") return;
        if (!await pagina.DisplayAlertAsync("Confirmar impresora", $"Se guardará {seleccion} y también quedará como predeterminada de Windows. La vista previa del sistema seguirá administrando copias e impresión.", "Guardar", "Cancelar")) return;
        EstablecerImpresoraPredeterminada(seleccion);
        Preferences.Default.Set(PreferenciaImpresora, seleccion);
        await pagina.DisplayAlertAsync("Impresoras", $"Impresora guardada: {seleccion}.", "Aceptar");
#else
        throw new PlatformNotSupportedException("La selección de impresoras instaladas solo está disponible en Windows.");
#endif
    }

    public Task PrepararImpresoraSeleccionadaAsync()
    {
#if WINDOWS
        var seleccionada = ImpresoraSeleccionada;
        if (string.IsNullOrWhiteSpace(seleccionada)) return Task.CompletedTask;
        if (!ListarImpresorasInstaladas().Contains(seleccionada, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"La impresora guardada ya no está disponible: {seleccionada}. Selecciona otra impresora.");
        if (!string.Equals(ObtenerImpresoraPredeterminada(), seleccionada, StringComparison.OrdinalIgnoreCase))
            EstablecerImpresoraPredeterminada(seleccionada);
        return Task.CompletedTask;
#else
        return Task.CompletedTask;
#endif
    }

    public async Task AbrirConfiguracionImpresorasAsync()
    {
#if WINDOWS
        await Launcher.Default.OpenAsync("ms-settings:printers");
#else
        throw new PlatformNotSupportedException("La configuración de impresoras de Windows solo está disponible en Windows.");
#endif
    }

#if WINDOWS
    private static IReadOnlyList<string> EnumerarImpresorasInstaladas()
    {
        EnumPrinters(ImpresorasLocalesYConectadas, null, 4, IntPtr.Zero, 0, out var bytesNecesarios, out _);
        if (bytesNecesarios == 0) return Array.Empty<string>();
        var buffer = Marshal.AllocHGlobal(checked((int)bytesNecesarios));
        try
        {
            if (!EnumPrinters(ImpresorasLocalesYConectadas, null, 4, buffer, bytesNecesarios, out _, out var cantidad))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo obtener la lista de impresoras de Windows.");
            var tamano = Marshal.SizeOf<PrinterInfo4>();
            var impresoras = new List<string>(checked((int)cantidad));
            for (var indice = 0; indice < cantidad; indice++)
            {
                var info = Marshal.PtrToStructure<PrinterInfo4>(IntPtr.Add(buffer, checked((int)indice * tamano)));
                var nombre = Marshal.PtrToStringUni(info.Nombre);
                if (!string.IsNullOrWhiteSpace(nombre)) impresoras.Add(nombre);
            }
            return impresoras.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(nombre => nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ObtenerImpresoraPredeterminada()
    {
        uint longitud = 0;
        GetDefaultPrinter(null, ref longitud);
        if (longitud == 0) return string.Empty;
        var buffer = new StringBuilder(checked((int)longitud));
        if (!GetDefaultPrinter(buffer, ref longitud)) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo consultar la impresora predeterminada de Windows.");
        return buffer.ToString();
    }

    private static void EstablecerImpresoraPredeterminada(string nombre)
    {
        if (!SetDefaultPrinter(nombre)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"No se pudo establecer {nombre} como impresora predeterminada.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo4
    {
        public IntPtr Nombre;
        public IntPtr Servidor;
        public uint Atributos;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, string? nombre, uint nivel, IntPtr buffer, uint bytesBuffer, out uint bytesNecesarios, out uint cantidad);

    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinter(StringBuilder? buffer, ref uint longitud);

    [DllImport("winspool.drv", EntryPoint = "SetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultPrinter(string nombre);
#endif
}
