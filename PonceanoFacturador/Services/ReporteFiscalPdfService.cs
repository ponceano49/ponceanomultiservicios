using System.Globalization;
using PonceanoFacturador.Services;
using SkiaSharp;

namespace PonceanoFacturador.Services;

public sealed class ReporteFiscalPdfService
{
    private static readonly CultureInfo MonedaCultura = CultureInfo.GetCultureInfo("es-DO");

    public async Task<string> CrearAsync(ReporteFiscalOcr reporte, string ruta)
    {
        ArgumentNullException.ThrowIfNull(reporte);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruta);
        using var fuenteStream = await FileSystem.OpenAppPackageFileAsync("pdf-opensans.ttf");
        using var negritaStream = await FileSystem.OpenAppPackageFileAsync("pdf-opensans-semibold.ttf");
        using var fuente = SKTypeface.FromStream(fuenteStream) ?? throw new InvalidOperationException("No se pudo cargar la fuente del reporte fiscal.");
        using var negrita = SKTypeface.FromStream(negritaStream) ?? throw new InvalidOperationException("No se pudo cargar la fuente del reporte fiscal.");
        using var salida = new MemoryStream();
        using (var documento = SKDocument.CreatePdf(salida) ?? throw new InvalidOperationException("No se pudo crear el PDF del borrador fiscal."))
        using (var dibujo = new DibujoReporte(documento, fuente, negrita, reporte))
        {
            dibujo.Texto(reporte.Advertencia, true);
            dibujo.Texto($"RNC compañía: {reporte.RncCompania}");
            dibujo.Texto($"Generado: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
            dibujo.Linea();
            foreach (var fila in reporte.Registros)
            {
                dibujo.Texto($"NCF: {fila.Datos.Ncf ?? "No disponible"} | RNC/Cédula: {fila.Datos.RncCedula ?? "No disponible"}", true);
                dibujo.Texto($"Tipo: {fila.Datos.TipoGastoOIngreso ?? "Pendiente"} | Comprobante: {fila.Datos.FechaComprobante ?? "Pendiente"} | Pago: {fila.Datos.FechaPago ?? "Pendiente"}");
                dibujo.Texto($"Bienes: {Monto(fila.Datos.MontoGravadoBienes)} | Servicios: {Monto(fila.Datos.MontoGravadoServicios)} | Exento: {Monto(fila.Datos.MontoExento)}");
                dibujo.Texto($"Total: {Monto(fila.Datos.TotalMontoFacturado)} | ITBIS: {Monto(fila.Datos.ItbisFacturado)} | ITBIS retenido: {Monto(fila.Datos.ItbisRetenido)}");
                dibujo.Texto($"ISR retenido: {Monto(fila.Datos.IsrRetenido)} | Otros impuestos: {Monto(fila.Datos.OtrosImpuestos)} | Propina: {Monto(fila.Datos.PropinaLegal)}");
                dibujo.Texto($"Forma de pago: {fila.Datos.FormaPago ?? "Pendiente"}");
                if (fila.Supuestos.Length > 0) dibujo.Texto($"SUPUESTOS: {string.Join(", ", fila.Supuestos)}", true);
                if (fila.Pendientes.Length > 0) dibujo.Texto($"PENDIENTES: {string.Join(", ", fila.Pendientes)}", true);
                dibujo.Linea();
            }
            foreach (var excluido in reporte.Excluidos) dibujo.Texto($"EXCLUIDO: {excluido}", true);
            dibujo.Finalizar();
            documento.Close();
        }
        await File.WriteAllBytesAsync(ruta, salida.ToArray());
        return ruta;
    }

    private static string Monto(decimal? valor) => valor?.ToString("N2", MonedaCultura) ?? "Pendiente";

    private sealed class DibujoReporte : IDisposable
    {
        private const float Ancho = 595, Alto = 842, Margen = 42, Pie = 45;
        private readonly SKDocument _documento;
        private readonly SKTypeface _fuente;
        private readonly SKTypeface _negrita;
        private readonly string _titulo;
        private readonly SKPaint _pintura = new() { IsAntialias = true, Color = SKColors.Black };
        private SKCanvas _canvas = null!;
        private float _y;
        private bool _paginaAbierta;

        public DibujoReporte(SKDocument documento, SKTypeface fuente, SKTypeface negrita, ReporteFiscalOcr reporte)
        {
            _documento = documento; _fuente = fuente; _negrita = negrita;
            _titulo = $"BORRADOR INTERNO {reporte.Tipo} — NO OFICIAL DGII";
            ComenzarPagina();
        }

        private void ComenzarPagina()
        {
            if (_paginaAbierta) _documento.EndPage();
            _canvas = _documento.BeginPage(Ancho, Alto);
            _paginaAbierta = true;
            _y = Margen;
            using var fuente = new SKFont(_negrita, 12);
            _y += 18;
            _canvas.DrawText(_titulo, Margen, _y, SKTextAlign.Left, fuente, _pintura);
            _y += 10;
        }

        private void Asegurar(float alto) { if (_y + alto > Alto - Pie) ComenzarPagina(); }

        public void Texto(string texto, bool destacado = false)
        {
            using var fuente = new SKFont(destacado ? _negrita : _fuente, destacado ? 9 : 8);
            foreach (var linea in Partir(texto, fuente, Ancho - 2 * Margen))
            {
                Asegurar(fuente.Size + 5);
                _y += fuente.Size + 3;
                _canvas.DrawText(linea, Margen, _y, SKTextAlign.Left, fuente, _pintura);
            }
        }

        private static IEnumerable<string> Partir(string texto, SKFont fuente, float ancho)
        {
            var linea = string.Empty;
            foreach (var palabra in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidata = linea.Length == 0 ? palabra : $"{linea} {palabra}";
                if (linea.Length > 0 && fuente.MeasureText(candidata) > ancho) { yield return linea; linea = palabra; }
                else linea = candidata;
            }
            if (linea.Length > 0) yield return linea;
        }

        public void Linea()
        {
            Asegurar(10); _y += 4;
            using var trazo = new SKPaint { Color = new SKColor(180, 180, 180), StrokeWidth = 0.6f };
            _canvas.DrawLine(Margen, _y, Ancho - Margen, _y, trazo); _y += 5;
        }

        public void Finalizar()
        {
            if (!_paginaAbierta) return;
            _documento.EndPage();
            _paginaAbierta = false;
        }

        public void Dispose() { Finalizar(); _pintura.Dispose(); }
    }
}
