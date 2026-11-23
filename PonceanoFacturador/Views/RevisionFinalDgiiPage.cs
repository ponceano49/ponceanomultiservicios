using System.Text;
using System.Text.RegularExpressions;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public sealed class RevisionFinalDgiiPage : ContentPage
{
    private sealed record RegistroRevision(string Ncf, bool Valido, string Estado, string Detalle);

    private static readonly Regex ErrorLinea = new(@"^Línea ([0-9]+):\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly DatabaseService _db;
    private readonly FormatoEnvioDgiiService _formatoDgii;
    private readonly string _usuarioId;
    private readonly string _tipo;
    private readonly DateTime _periodo;
    private readonly string _rnc;
    private readonly Func<ResultadoExportacionFiscal, Task> _archivoCorregido;
    private readonly Func<string, FileResult, bool, string, Task> _estadoActualizado;
    private readonly Func<string, FileResult, Task> _enviarManual;
    private readonly Label _resumen = new() { FontAttributes = FontAttributes.Bold, FontSize = 16 };
    private readonly Label _estado = new() { TextColor = Color.FromArgb("#9A5A00") };
    private readonly Label _erroresGenerales = new() { TextColor = Color.FromArgb("#C62828") };
    private readonly CollectionView _registros = new()
    {
        ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = 6 },
        ItemTemplate = new DataTemplate(() =>
        {
            var marca = new Label { FontSize = 17, VerticalOptions = LayoutOptions.Start };
            marca.SetBinding(Label.TextProperty, nameof(RegistroRevision.Estado));
            marca.SetBinding(Label.TextColorProperty, new Binding(nameof(RegistroRevision.Valido), converter: new EstadoRevisionColorConverter()));
            var ncf = new Label { FontAttributes = FontAttributes.Bold };
            ncf.SetBinding(Label.TextProperty, nameof(RegistroRevision.Ncf));
            var detalle = new Label { FontSize = 12, TextColor = Color.FromArgb("#60788F"), LineBreakMode = LineBreakMode.WordWrap };
            detalle.SetBinding(Label.TextProperty, nameof(RegistroRevision.Detalle));
            var texto = new VerticalStackLayout { Spacing = 2, Children = { ncf, detalle } };
            return new Border
            {
                Padding = 10,
                Stroke = Color.FromArgb("#D5DEE5"),
                Content = new HorizontalStackLayout { Spacing = 8, Children = { marca, texto } }
            };
        })
    };
    private readonly Button _corregir = new() { Text = "📝 CORREGIR" };
    private readonly Button _enviar = new() { Text = "📤 ENVIAR A LA DGII", IsEnabled = false };
    private readonly Button _cerrar = new() { Text = "💾 CERRAR Y GUARDAR" };
    private FileResult _archivo;
    private ResultadoValidacionFormatoDgii? _validacion;
    private int _cantidadCorrectos;
    private int _cantidadPendientes;
    private bool _procesando;

    private sealed class EstadoRevisionColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is true ? Color.FromArgb("#2E7D32") : Color.FromArgb("#C62828");
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public RevisionFinalDgiiPage(DatabaseService db, FormatoEnvioDgiiService formatoDgii, string usuarioId,
        string tipo, DateTime periodo, string rnc, FileResult archivo,
        Func<ResultadoExportacionFiscal, Task> archivoCorregido,
        Func<string, FileResult, bool, string, Task> estadoActualizado,
        Func<string, FileResult, Task> enviarManual)
    {
        _db = db;
        _formatoDgii = formatoDgii;
        _usuarioId = usuarioId;
        _tipo = tipo;
        _periodo = periodo;
        _rnc = rnc;
        _archivo = archivo;
        _archivoCorregido = archivoCorregido;
        _estadoActualizado = estadoActualizado;
        _enviarManual = enviarManual;
        Title = "Revisión final DGII";
        BackgroundColor = Colors.White;

        _corregir.Clicked += CorregirAsync;
        _enviar.Clicked += EnviarManualAsync;
        _cerrar.Clicked += CerrarYGuardarAsync;

        var botones = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)
            },
            ColumnSpacing = 8,
            Children = { _corregir, _enviar, _cerrar }
        };
        Grid.SetColumn(_corregir, 0);
        Grid.SetColumn(_enviar, 1);
        Grid.SetColumn(_cerrar, 2);

        Content = new Grid
        {
            Padding = 18,
            RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)
            },
            RowSpacing = 10,
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 6,
                    Children =
                    {
                        new Label { Text = $"REVISIÓN FINAL PARA ENVÍO DGII ({tipo} {(tipo == "606" ? "COMPRAS" : "VENTAS")})", FontSize = 21, FontAttributes = FontAttributes.Bold },
                        _resumen,
                        _erroresGenerales,
                        new Label { Text = "✅ Validación estructural local. El envío se realiza manualmente en la Oficina Virtual; la API DGII sigue pendiente de activación.", TextColor = Color.FromArgb("#9A5A00") }
                    }
                },
                _registros,
                new VerticalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        _estado,
                        botones,
                        new Label { Text = "Cerrar guarda una copia local y el estado de revisión. No envía el archivo ni certifica su aceptación por la DGII.", FontSize = 12, TextColor = Color.FromArgb("#60788F") }
                    }
                }
            }
        };
        if (Content is not Grid grid) throw new InvalidOperationException("No se pudo crear el diseño de revisión.");
        grid.SetRow(_registros, 1);
        grid.SetRow(grid.Children[2], 2);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RevisarArchivoAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        if (!_procesando) CerrarYGuardarAsync(this, EventArgs.Empty);
        return true;
    }

    private async Task RevisarArchivoAsync()
    {
        if (_procesando) return;
        _procesando = true;
        _corregir.IsEnabled = _enviar.IsEnabled = _cerrar.IsEnabled = false;
        try
        {
            if (!string.Equals(Path.GetExtension(_archivo.FileName), ".txt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("El archivo de revisión debe ser TXT.");
            await using var stream = await _archivo.OpenReadAsync();
            using var contenido = new MemoryStream();
            await stream.CopyToAsync(contenido);
            var bytes = contenido.ToArray();
            contenido.Position = 0;
            _validacion = await FormatoEnvioDgiiService.ValidarArchivoAsync(contenido, _tipo, _rnc, _periodo.Year, _periodo.Month);
            using var lector = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
            var texto = await lector.ReadToEndAsync();
            var lineas = texto.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            var erroresPorLinea = new Dictionary<int, List<string>>();
            var erroresGlobales = new List<string>();
            foreach (var error in _validacion.Errores)
            {
                var coincidencia = ErrorLinea.Match(error);
                if (!coincidencia.Success || !int.TryParse(coincidencia.Groups[1].Value, out var numeroLinea))
                {
                    erroresGlobales.Add(error);
                    continue;
                }
                if (!erroresPorLinea.TryGetValue(numeroLinea, out var lista))
                    erroresPorLinea[numeroLinea] = lista = new List<string>();
                lista.Add(coincidencia.Groups[2].Value);
            }

            var detalles = new List<RegistroRevision>();
            var numeroRegistro = 0;
            for (var indice = 1; indice < lineas.Length; indice++)
            {
                numeroRegistro++;
                var campos = lineas[indice].Split('|');
                var indiceNcf = _tipo == "606" ? 3 : 2;
                var ncf = campos.Length > indiceNcf && !string.IsNullOrWhiteSpace(campos[indiceNcf])
                    ? campos[indiceNcf].Trim()
                    : $"Línea {indice + 1}";
                var errores = erroresPorLinea.GetValueOrDefault(indice + 1) ?? new List<string>();
                var valido = _validacion.Valido && errores.Count == 0;
                var detalle = errores.Count > 0 ? string.Join(Environment.NewLine, errores) :
                    erroresGlobales.Count > 0 ? string.Join(Environment.NewLine, erroresGlobales) :
                    valido ? "Registro pasó las comprobaciones estructurales locales." : "Pendiente: el archivo contiene errores generales de estructura.";
                detalles.Add(new RegistroRevision(ncf, valido, valido ? "✅ Correcto" : "⚠️ Con errores", detalle));
            }

            _cantidadCorrectos = detalles.Count(registro => registro.Valido);
            _cantidadPendientes = detalles.Count - _cantidadCorrectos;
            _registros.ItemsSource = detalles;
            _resumen.Text = $"Total registros: {detalles.Count} | Correctos: {_cantidadCorrectos} | Pendientes por corregir: {_cantidadPendientes}";
            _erroresGenerales.Text = erroresGlobales.Count == 0 ? string.Empty :
                "Errores generales del archivo:\n" + string.Join(Environment.NewLine, erroresGlobales);
            _enviar.IsEnabled = _validacion.Valido && _cantidadPendientes == 0 && detalles.Count > 0;
            _corregir.IsEnabled = _cantidadPendientes > 0;
            _estado.Text = _enviar.IsEnabled
                ? "Sin errores locales pendientes. Debe confirmar manualmente el archivo con la herramienta oficial DGII."
                : "Corrija los errores pendientes antes de abrir la Oficina Virtual para este archivo.";
            await _estadoActualizado(_tipo, _archivo, _validacion.Valido && _cantidadPendientes == 0, _estado.Text);
        }
        catch (Exception ex)
        {
            _validacion = null;
            _cantidadCorrectos = 0;
            _cantidadPendientes = 0;
            _registros.ItemsSource = Array.Empty<RegistroRevision>();
            _resumen.Text = "No se pudo completar la revisión del archivo.";
            _erroresGenerales.Text = ex.Message;
            _estado.Text = "El envío manual permanece bloqueado hasta validar un TXT accesible.";
            _enviar.IsEnabled = false;
            _corregir.IsEnabled = false;
            await _estadoActualizado(_tipo, _archivo, false, _estado.Text);
        }
        finally
        {
            _procesando = false;
            _cerrar.IsEnabled = true;
        }
    }

    private async void CorregirAsync(object? sender, EventArgs e)
    {
        if (_cantidadPendientes == 0 || _procesando) return;
        try
        {
            var problemas = await _db.RevalidarRegistrosFiscalesAsync(_usuarioId, _tipo, _periodo.Year, _periodo.Month);
            if (problemas.Count == 0)
            {
                await DisplayAlertAsync("Corrección fiscal", "Los errores del TXT no coinciden con registros guardados con errores para este período. Verifica que el archivo corresponda a los registros locales.", "Aceptar");
                return;
            }
            var pagina = new CorreccionRegistrosFiscalesPage(_db, _formatoDgii, _usuarioId, _tipo, _periodo, _rnc,
                async resultado =>
                {
                    await _archivoCorregido(resultado);
                    _archivo = new FileResult(resultado.Ruta);
                    await RevisarArchivoAsync();
                });
            await Navigation.PushAsync(pagina);
        }
        catch (Exception ex)
        {
            _estado.Text = $"No se pudo abrir la corrección fiscal: {ex.Message}";
        }
    }

    private async void EnviarManualAsync(object? sender, EventArgs e)
    {
        if (_validacion?.Valido != true || _cantidadPendientes != 0 || _procesando) return;
        try
        {
            _enviar.IsEnabled = false;
            await _enviarManual(_tipo, _archivo);
            await RevisarArchivoAsync();
        }
        catch (Exception ex)
        {
            _estado.Text = $"No se pudo abrir la Oficina Virtual DGII: {ex.Message}";
        }
        finally { _enviar.IsEnabled = _validacion?.Valido == true && _cantidadPendientes == 0; }
    }

    private async void CerrarYGuardarAsync(object? sender, EventArgs e)
    {
        if (_procesando) return;
        _procesando = true;
        _cerrar.IsEnabled = false;
        string? ruta = null;
        try
        {
            var usuario = _usuarioId;
#if WINDOWS
            var documentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documentos)) throw new InvalidOperationException("No se encontró la carpeta Documentos.");
            var carpeta = Path.Combine(documentos, "DGII Automática", $"Archivos {_tipo}");
#else
            var carpeta = Path.Combine(FileSystem.AppDataDirectory, $"Archivos {_tipo}");
#endif
            Directory.CreateDirectory(carpeta);
            var nombreBase = $"{_tipo}_REVISION_{_periodo:MM_yyyy}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Path.GetFileNameWithoutExtension(_archivo.FileName)}.txt";
            ruta = Path.Combine(carpeta, nombreBase);
            await using (var entrada = await _archivo.OpenReadAsync())
            await using (var salida = new FileStream(ruta, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await entrada.CopyToAsync(salida);

            var estado = _validacion?.Valido == true && _cantidadPendientes == 0
                ? "Preparado para envío manual; revisión estructural local superada, no enviado"
                : "Revisión local con errores; pendiente de corrección, no enviado";
            await _db.RegistrarExportacionDGIIAsync(new HistorialExportacionDGII
            {
                TipoReporte = _tipo,
                PeriodoMes = _periodo.Month,
                PeriodoAnio = _periodo.Year,
                IdUsuario = usuario,
                NombreArchivo = Path.GetFileName(ruta),
                RutaGuardado = ruta,
                CantidadRegistros = _validacion?.CantidadRegistros ?? 0,
                Estado = estado,
                ObservacionesDGII = "Copia guardada al cerrar la revisión final. No se realizó envío ni certificación oficial DGII."
            });
            _archivo = new FileResult(ruta);
            await _estadoActualizado(_tipo, _archivo, _validacion?.Valido == true && _cantidadPendientes == 0, estado);
            _estado.Text = $"✅ Archivo y estado guardados: {ruta}. No se envió a la DGII.";
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            if (ruta is not null && File.Exists(ruta)) File.Delete(ruta);
            _estado.Text = $"No se pudo guardar el archivo y su estado; la revisión sigue abierta: {ex.Message}";
            _cerrar.IsEnabled = true;
        }
        finally { _procesando = false; }
    }
}
