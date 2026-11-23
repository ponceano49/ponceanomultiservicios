using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class ArticulosPage : ContentPage, IShellToolbarActionPage
{
    public Button ShellToolbarActionButton => NuevoButton;

    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly ILogger<ArticulosPage> _logger;
    private List<Articulo> _articulos = new();
    private bool? _mobileHeader;

    public ArticulosPage(DatabaseService db, AuthService auth, ILogger<ArticulosPage> logger)
    {
        InitializeComponent();
        _db = db; _auth = auth; _logger = logger;
#if ANDROID
        TituloHeader.IsVisible = NuevoButton.IsVisible = false;
#endif
        ToolbarItems.Remove(ToolbarItems.First(item => item.Text == "+ Nuevo"));
    }

    private void OnHeaderSizeChanged(object? sender, EventArgs e)
    {
        var mobile = HeaderGrid.Width < 600;
        if (_mobileHeader == mobile) return;
        _mobileHeader = mobile;
        HeaderGrid.ColumnDefinitions.Clear();
        HeaderGrid.RowDefinitions.Clear();
        if (mobile)
        {
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(ImportarExportarMenu, 0);
            Grid.SetColumn(ImportarExportarMenu, 0);
            Grid.SetRow(BuscarBar, 1);
            Grid.SetColumn(BuscarBar, 0);
            Grid.SetColumnSpan(BuscarBar, 3);
            BuscarBar.HeightRequest = 48;
        }
        else
        {
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Star, new GridLength(360) })
                HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(width));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(TituloHeader, 0);
            Grid.SetColumn(TituloHeader, 0);
            Grid.SetRow(NuevoButton, 0);
            Grid.SetColumn(NuevoButton, 1);
            Grid.SetRow(ImportarExportarMenu, 0);
            Grid.SetColumn(ImportarExportarMenu, 2);
            Grid.SetRow(BuscarBar, 0);
            Grid.SetColumn(BuscarBar, 4);
            Grid.SetColumnSpan(BuscarBar, 1);
            BuscarBar.HeightRequest = 38;
        }
    }

    protected override async void OnAppearing() { base.OnAppearing(); await CargarAsync(); }

    private async Task CargarAsync()
    {
        try
        {
            _articulos = await _db.GetArticulosAsync();
            AplicarBusqueda();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudieron cargar los artículos."); await DisplayAlertAsync("Artículos", "No se pudieron cargar los artículos.", "Aceptar"); }
    }

    private void OnBuscarChanged(object? sender, TextChangedEventArgs e) => AplicarBusqueda();

    private void AplicarBusqueda()
    {
        var texto = BuscarBar?.Text?.Trim() ?? string.Empty;
        Lista.ItemsSource = string.IsNullOrEmpty(texto) ? _articulos : _articulos.Where(a => a.Descripcion.Contains(texto, StringComparison.CurrentCultureIgnoreCase) || a.Codigo.Contains(texto, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void OnMenuPointerEntered(object? sender, PointerEventArgs e) => ImportarExportarPopup.IsVisible = true;
    private void OnMenuPointerExited(object? sender, PointerEventArgs e) => ImportarExportarPopup.IsVisible = false;
    private void OnMenuClicked(object? sender, EventArgs e) => ImportarExportarPopup.IsVisible = !ImportarExportarPopup.IsVisible;

    private async void OnNuevoClicked(object? sender, EventArgs e) => await EditarAsync(null);
    private async void OnEditarFilaClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: Articulo articulo }) await EditarAsync(articulo);
    }
    private async void OnEliminarFilaClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: Articulo articulo }) await EliminarAsync(articulo);
    }

    private async Task EditarAsync(Articulo? original)
    {
        var codigo = await DisplayPromptAsync(original == null ? "Nuevo artículo" : "Editar artículo", "Código:", initialValue: original?.Codigo);
        if (codigo == null) return;
        var descripcion = await DisplayPromptAsync(original == null ? "Nuevo artículo" : "Editar artículo", "Descripción:", initialValue: original?.Descripcion);
        if (descripcion == null) return;
        var precioTexto = await DisplayPromptAsync("Precio", "Precio unitario:", initialValue: original?.Precio.ToString(System.Globalization.CultureInfo.InvariantCulture), keyboard: Keyboard.Numeric);
        if (precioTexto == null) return;
        if (!decimal.TryParse(precioTexto, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var precio))
        { await DisplayAlertAsync("Artículos", "El precio no es válido.", "Aceptar"); return; }
        var stockTexto = await DisplayPromptAsync("Inventario", "Cantidad en inventario:", initialValue: original?.Stock.ToString() ?? "0", keyboard: Keyboard.Numeric);
        if (stockTexto == null) return;
        if (!int.TryParse(stockTexto, out var stock)) { await DisplayAlertAsync("Artículos", "El inventario debe ser un número entero.", "Aceptar"); return; }
        var minimoTexto = await DisplayPromptAsync("Inventario mínimo", "Avisar cuando el stock llegue a:", initialValue: original?.StockMinimo.ToString() ?? "0", keyboard: Keyboard.Numeric);
        if (minimoTexto == null) return;
        if (!int.TryParse(minimoTexto, out var minimo) || minimo < 0) { await DisplayAlertAsync("Artículos", "El mínimo debe ser un entero no negativo.", "Aceptar"); return; }
        var costoTexto = await DisplayPromptAsync("Costo", "Último costo unitario de compra:", initialValue: original?.UltimoCosto.ToString(CultureInfo.InvariantCulture) ?? "0.00", keyboard: Keyboard.Numeric);
        if (costoTexto == null) return;
        if (!decimal.TryParse(costoTexto, NumberStyles.Number, CultureInfo.InvariantCulture, out var costo) || costo < 0) { await DisplayAlertAsync("Artículos", "El último costo debe ser un importe válido.", "Aceptar"); return; }
        var tipoBienServicio = await DisplayActionSheetAsync("Clasificación fiscal del artículo", "Cancelar", null, "Bienes", "Servicios", "Pendiente");
        if (tipoBienServicio is null or "Cancelar") return;
        var motivo = original is null ? string.Empty : await DisplayPromptAsync("Auditoría de producto", "Motivo del cambio (opcional):", maxLength: 500);
        if (motivo is null) return;
        try { await _db.GuardarArticuloAsync(new Articulo { Id = original?.Id ?? 0, Codigo = codigo.Trim(), Descripcion = descripcion.Trim(), Precio = precio, Itbis = original?.Itbis ?? 18m, Stock = stock, StockMinimo = minimo, UltimoCosto = costo, TipoBienServicio = tipoBienServicio }, _auth.CurrentUser?.IdFirebase ?? string.Empty, _auth.CurrentUser?.Nombre ?? string.Empty, motivo); await CargarAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo guardar el artículo."); await DisplayAlertAsync("Artículos", ex.Message, "Aceptar"); }
    }

    private async Task EliminarAsync(Articulo articulo)
    {
        try
        {
            if (!await DisplayAlertAsync("Eliminar artículo", $"¿Eliminar {articulo.Descripcion}?", "Eliminar", "Cancelar")) return;
            await _db.EliminarArticuloAsync(articulo.Id);
            await CargarAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo eliminar el artículo."); await DisplayAlertAsync("Artículos", "No se pudo eliminar el artículo.", "Aceptar"); }
    }
    private async void OnExportarClicked(object? sender, EventArgs e)
    {
        ImportarExportarPopup.IsVisible = false;
        try
        {
            var filas = new List<string> { "Código,Descripción,Precio,ITBIS,Inventario,StockMínimo,ÚltimoCosto,TipoBienServicio" };
            filas.AddRange((await _db.GetArticulosAsync()).Select(articulo => string.Join(',',
                CampoCsv(articulo.Codigo), CampoCsv(articulo.Descripcion),
                articulo.Precio.ToString("0.00", CultureInfo.InvariantCulture),
                articulo.Itbis.ToString("0.##", CultureInfo.InvariantCulture),
                articulo.Stock.ToString(CultureInfo.InvariantCulture),
                articulo.StockMinimo.ToString(CultureInfo.InvariantCulture),
                articulo.UltimoCosto.ToString("0.00", CultureInfo.InvariantCulture),
                CampoCsv(articulo.TipoBienServicio))));
            var ruta = Path.Combine(FileSystem.CacheDirectory, $"articulos-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            await File.WriteAllTextAsync(ruta, string.Join(Environment.NewLine, filas), new UTF8Encoding(true));
            await Share.Default.RequestAsync(new ShareFileRequest("Exportar artículos CSV", new ShareFile(ruta)));
            EstadoLabel.Text = "CSV de artículos preparado para compartir o guardar.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron exportar los artículos.");
            EstadoLabel.Text = "No se pudieron exportar los artículos. Inténtalo de nuevo.";
        }
    }

    private async void OnImportarClicked(object? sender, EventArgs e)
    {
        ImportarExportarPopup.IsVisible = false;
        try
        {
            var tiposArchivo = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = new[] { ".csv" },
                [DevicePlatform.Android] = new[] { "text/csv", "text/comma-separated-values" },
                [DevicePlatform.iOS] = new[] { "public.comma-separated-values-text" },
                [DevicePlatform.MacCatalyst] = new[] { "public.comma-separated-values-text" }
            });
            var archivo = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Selecciona el CSV de artículos", FileTypes = tiposArchivo });
            if (archivo is null) return;

            await using var stream = await archivo.OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var contenido = await reader.ReadToEndAsync();
            var filas = ParsearCsv(contenido);
            var cantidadImportada = await ImportarArticulosAsync(filas);
            EstadoLabel.Text = $"Importación completada: {cantidadImportada} artículo(s) agregado(s); los códigos existentes se omitieron.";
            await CargarAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron importar los artículos desde CSV.");
            EstadoLabel.Text = ex is InvalidOperationException ? ex.Message : "No se pudieron importar los artículos. Revisa el archivo e inténtalo de nuevo.";
        }
    }

    private async Task<int> ImportarArticulosAsync(List<List<string>> filas)
    {
        if (filas.Count < 2) throw new InvalidOperationException("El CSV no contiene artículos para importar.");
        var encabezados = filas[0].Select(valor => valor.Trim().TrimStart('\uFEFF')).ToList();
        var columnas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < encabezados.Count; index++)
            if (!columnas.TryAdd(encabezados[index], index)) throw new InvalidOperationException("El CSV contiene encabezados duplicados.");
        var requeridos = new[] { "Código", "Descripción", "Precio", "ITBIS", "Inventario" };
        if (requeridos.Any(nombre => !columnas.ContainsKey(nombre)))
            throw new InvalidOperationException("El CSV debe incluir las columnas: Código, Descripción, Precio, ITBIS e Inventario.");

        var existentes = await _db.GetArticulosAsync();
        var codigosExistentes = existentes.Select(articulo => articulo.Codigo.Trim()).Where(codigo => codigo.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nuevos = new List<Articulo>();
        for (var indiceFila = 1; indiceFila < filas.Count; indiceFila++)
        {
            var fila = filas[indiceFila];
            if (fila.All(string.IsNullOrWhiteSpace)) continue;
            var numeroFila = indiceFila + 1;
            if (requeridos.Any(nombre => columnas[nombre] >= fila.Count))
                throw new InvalidOperationException($"La fila {numeroFila} no contiene todas las columnas requeridas.");
            var codigo = fila[columnas["Código"]].Trim();
            if (codigo.Length > 0 && !codigosExistentes.Add(codigo)) continue;
            var descripcion = fila[columnas["Descripción"]].Trim();
            if (descripcion.Length is 0 or > 250) throw new InvalidOperationException($"La descripción de la fila {numeroFila} es obligatoria y debe tener hasta 250 caracteres.");
            if (codigo.Length > 50) throw new InvalidOperationException($"El código de la fila {numeroFila} supera los 50 caracteres.");
            if (!decimal.TryParse(fila[columnas["Precio"]], NumberStyles.Number, CultureInfo.InvariantCulture, out var precio) || precio < 0)
                throw new InvalidOperationException($"El precio de la fila {numeroFila} no es válido.");
            if (!decimal.TryParse(fila[columnas["ITBIS"]], NumberStyles.Number, CultureInfo.InvariantCulture, out var itbis) || itbis is < 0 or > 100)
                throw new InvalidOperationException($"El ITBIS de la fila {numeroFila} debe estar entre 0 y 100.");
            if (!int.TryParse(fila[columnas["Inventario"]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stock) || stock < 0)
                throw new InvalidOperationException($"El inventario de la fila {numeroFila} debe ser un entero no negativo.");
            var minimo = 0;
            if (columnas.TryGetValue("StockMínimo", out var indiceMinimo) && indiceMinimo < fila.Count &&
                (!int.TryParse(fila[indiceMinimo], NumberStyles.Integer, CultureInfo.InvariantCulture, out minimo) || minimo < 0))
                throw new InvalidOperationException($"El stock mínimo de la fila {numeroFila} debe ser un entero no negativo.");
            var costo = 0m;
            if (columnas.TryGetValue("ÚltimoCosto", out var indiceCosto) && indiceCosto < fila.Count &&
                (!decimal.TryParse(fila[indiceCosto], NumberStyles.Number, CultureInfo.InvariantCulture, out costo) || costo < 0))
                throw new InvalidOperationException($"El último costo de la fila {numeroFila} no es válido.");
            var tipoBienServicio = "Pendiente";
            if (columnas.TryGetValue("TipoBienServicio", out var indiceTipo) && indiceTipo < fila.Count && !string.IsNullOrWhiteSpace(fila[indiceTipo]))
            {
                tipoBienServicio = fila[indiceTipo].Trim();
                if (tipoBienServicio is not ("Bienes" or "Servicios" or "Pendiente"))
                    throw new InvalidOperationException($"La clasificación fiscal de la fila {numeroFila} debe ser Bienes, Servicios o Pendiente.");
            }
            nuevos.Add(new Articulo { Codigo = codigo, Descripcion = descripcion, Precio = precio, Itbis = itbis, Stock = stock, StockMinimo = minimo, UltimoCosto = costo, TipoBienServicio = tipoBienServicio });
        }

        foreach (var articulo in nuevos) await _db.GuardarArticuloAsync(articulo, _auth.CurrentUser?.IdFirebase ?? string.Empty, _auth.CurrentUser?.Nombre ?? string.Empty, "Importación desde CSV");
        return nuevos.Count;
    }

    private static List<List<string>> ParsearCsv(string contenido)
    {
        var registros = new List<List<string>>();
        var registro = new List<string>();
        var campo = new StringBuilder();
        var entreComillas = false;
        for (var indice = 0; indice < contenido.Length; indice++)
        {
            var caracter = contenido[indice];
            if (entreComillas)
            {
                if (caracter == '"' && indice + 1 < contenido.Length && contenido[indice + 1] == '"')
                {
                    campo.Append('"');
                    indice++;
                }
                else if (caracter == '"') entreComillas = false;
                else campo.Append(caracter);
            }
            else if (caracter == '"') entreComillas = true;
            else if (caracter == ',')
            {
                registro.Add(campo.ToString());
                campo.Clear();
            }
            else if (caracter is '\r' or '\n')
            {
                registro.Add(campo.ToString());
                campo.Clear();
                registros.Add(registro);
                registro = new List<string>();
                if (caracter == '\r' && indice + 1 < contenido.Length && contenido[indice + 1] == '\n') indice++;
            }
            else campo.Append(caracter);
        }
        if (entreComillas) throw new InvalidOperationException("El CSV contiene un campo entre comillas sin cerrar.");
        if (campo.Length > 0 || registro.Count > 0)
        {
            registro.Add(campo.ToString());
            registros.Add(registro);
        }
        return registros;
    }

    private static string CampoCsv(string valor)
    {
        var contenido = valor.TrimStart();
        if (contenido.Length > 0 && contenido[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n') valor = "'" + valor;
        return "\"" + valor.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private async void OnVolverClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
