using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public sealed class Envio606Page : ContentPage
{
    private readonly AuthService _auth;
    private readonly DatabaseService _db;
    private readonly ILogger<Envio606Page> _logger;
    private readonly FormatoEnvioDgiiService _formatoDgii;
    private readonly Label _estadoCredenciales = new() { TextColor = Color.FromArgb("#60788F") };
    private readonly Entry _rnc = new() { Placeholder = "RNC / cédula del contribuyente", Keyboard = Keyboard.Numeric };
    private readonly Entry _contrasena = new() { Placeholder = "Contraseña Oficina Virtual DGII", IsPassword = true };
    private readonly Entry _token = new() { Placeholder = "Código de transacción / token", IsPassword = true };
    private readonly DatePicker _periodo = new() { Format = "MM/yyyy", Date = DateTime.Today };
    private readonly Label _archivo = new() { Text = "Ningún archivo seleccionado." };
    private readonly Label _resultado = new() { TextColor = Color.FromArgb("#9A5A00") };
    private readonly Button _abrirCarga = new() { Text = "ENVIAR ARCHIVO 606 (COMPRAS)", IsEnabled = false };
    private readonly DatePicker _periodo607 = new() { Format = "MM/yyyy", Date = DateTime.Today };
    private readonly Label _archivo607 = new() { Text = "Ningún archivo seleccionado." };
    private readonly Label _resultado607 = new() { TextColor = Color.FromArgb("#9A5A00") };
    private readonly Button _abrirCarga607 = new() { Text = "ENVIAR ARCHIVO 607 (VENTAS)", IsEnabled = false };
    private readonly Picker _selectorModulo = new() { Title = "Seleccionar Módulo DGII" };
    private readonly Grid _columnasModulos = new()
    {
        ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Star) },
        RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) },
        ColumnSpacing = 12,
        RowSpacing = 12
    };
    private readonly VerticalStackLayout _columna606 = new() { Spacing = 10, Padding = 12 };
    private readonly VerticalStackLayout _columna607 = new() { Spacing = 10, Padding = 12 };
    private FileResult? _seleccion;
    private FileResult? _seleccion607;
    private bool _enviado606;
    private bool _enviado607;
    private int _versionPantalla;
    private sealed record Credenciales(string Rnc, string Contrasena);

    public Envio606Page(AuthService auth, DatabaseService db, ILogger<Envio606Page> logger, FormatoEnvioDgiiService formatoDgii)
    {
        _auth = auth;
        _db = db;
        _logger = logger;
        _formatoDgii = formatoDgii;
        Title = "Carga y envío Formato 606";
        var guardar = new Button { Text = "Guardar RNC y contraseña de forma segura" };
        var borrar = new Button { Text = "Eliminar credenciales guardadas" };
        var seleccionar = new Button { Text = "Seleccionar TXT 606" };
        var validar = new Button { Text = "Validar archivo" };
        var abrirPortal = new Button { Text = "Abrir Oficina Virtual DGII (sin archivo)" };
        var generar = new Button { Text = "Generar borrador interno 606" };
        var corregir606 = new Button { Text = "📝 Corregir Registros Fiscales" };
        var herramienta = new Button { Text = "Abrir herramienta oficial 606 de la DGII" };
        var seleccionar607 = new Button { Text = "Seleccionar TXT 607" };
        var validar607 = new Button { Text = "Validar archivo 607" };
        var generar607 = new Button { Text = "Generar borrador estructurado 607" };
        var corregir607 = new Button { Text = "📝 Corregir Registros Fiscales" };
        var herramienta607 = new Button { Text = "Abrir herramienta oficial 607 de la DGII" };
        var abrirPortal607 = new Button { Text = "Abrir Oficina Virtual DGII (sin archivo) para 607" };
        var marcarEnviado606 = new Button { Text = "Marcar como ENVIADO a la DGII (606)" };
        var marcarEnviado607 = new Button { Text = "Marcar como ENVIADO a la DGII (607)" };
        var envioAutomatico = new Button
        {
            Text = "🚀 Envío Automático API DGII (PENDIENTE DE ACTIVACIÓN)",
            IsEnabled = false,
            BackgroundColor = Color.FromArgb("#D5D5D5"),
            TextColor = Color.FromArgb("#666666")
        };
        var avisoEnvioAutomatico = new Label
        {
            Text = "⚠️ ENVÍO AUTOMÁTICO POR API DGII: PENDIENTE DE ACTIVACIÓN. Requiere la API oficial y autorización escrita por la DGII. El flujo MANUAL para generar, validar y subir los archivos TXT se encuentra habilitado.",
            TextColor = Color.FromArgb("#9A5A00")
        };
        guardar.Clicked += GuardarCredenciales;
        borrar.Clicked += BorrarCredenciales;
        generar.Clicked += GenerarBorrador;
        corregir606.Clicked += async (_, _) => await AbrirCorreccionAsync("606");
        corregir607.Clicked += async (_, _) => await AbrirCorreccionAsync("607");
        herramienta.Clicked += AbrirHerramienta;
        seleccionar.Clicked += SeleccionarArchivo;
        validar.Clicked += ValidarArchivo;
        _abrirCarga.Clicked += AbrirConArchivo;
        abrirPortal.Clicked += AbrirPortal;
        seleccionar607.Clicked += SeleccionarArchivo607;
        validar607.Clicked += ValidarArchivo607;
        generar607.Clicked += GenerarBorrador607;
        herramienta607.Clicked += AbrirHerramienta607;
        _abrirCarga607.Clicked += AbrirConArchivo607;
        abrirPortal607.Clicked += AbrirPortal607;
        marcarEnviado606.Clicked += (_, _) => MarcarComoEnviadoAsync("606");
        marcarEnviado607.Clicked += (_, _) => MarcarComoEnviadoAsync("607");
        _rnc.TextChanged += (_, _) => Invalidar();
        _periodo.DateSelected += (_, _) => Invalidar("606");
        _periodo607.DateSelected += (_, _) => Invalidar("607");
        _selectorModulo.ItemsSource = new[] { "📘 Libro Compras — Formato 606", "📗 Libro Ventas — Formato 607" };
        _selectorModulo.SelectedIndex = 0;
        _selectorModulo.SelectedIndexChanged += OnModuloSeleccionado;

        _columna606.Children.Add(new Label { Text = "📘 LIBRO COMPRAS (606)", FontSize = 20 });
        _columna606.Children.Add(new Label { Text = "El borrador 606 existente sigue siendo interno. Los archivos externos se someten a comprobaciones estructurales locales, no a certificación oficial DGII.", TextColor = Color.FromArgb("#9A5A00") });
        _columna606.Children.Add(new Label { Text = "Período 606 (mes y año)" });
        _columna606.Children.Add(_periodo);
        _columna606.Children.Add(generar);
        _columna606.Children.Add(corregir606);
        _columna606.Children.Add(herramienta);
        _columna606.Children.Add(seleccionar);
        _columna606.Children.Add(_archivo);
        _columna606.Children.Add(validar);
        _columna606.Children.Add(_resultado);
        _columna606.Children.Add(_abrirCarga);
        _columna606.Children.Add(marcarEnviado606);
        _columna606.Children.Add(abrirPortal);

        _columna607.Children.Add(new Label { Text = "📗 LIBRO VENTAS (607)", FontSize = 20 });
        _columna607.Children.Add(new Label { Text = "El TXT generado es un borrador local basado en los campos disponibles. Confírmalo con la herramienta oficial DGII antes de presentarlo.", TextColor = Color.FromArgb("#9A5A00") });
        _columna607.Children.Add(new Label { Text = "Período 607 (mes y año)" });
        _columna607.Children.Add(_periodo607);
        _columna607.Children.Add(generar607);
        _columna607.Children.Add(corregir607);
        _columna607.Children.Add(herramienta607);
        _columna607.Children.Add(seleccionar607);
        _columna607.Children.Add(_archivo607);
        _columna607.Children.Add(validar607);
        _columna607.Children.Add(_resultado607);
        _columna607.Children.Add(_abrirCarga607);
        _columna607.Children.Add(marcarEnviado607);
        _columna607.Children.Add(abrirPortal607);
        _columnasModulos.Add(_columna606, 0, 0);
        _columnasModulos.Add(_columna607, 1, 0);
        _columnasModulos.SizeChanged += OnColumnasModulosSizeChanged;

        Content = new ScrollView { Content = new VerticalStackLayout
        {
            Padding = 24, Spacing = 12, MaximumWidthRequest = 1000,
            Children =
            {
                new Label { Text = "DGII AUTOMÁTICA — LIBRO 606 (COMPRAS) / LIBRO 607 (VENTAS)", FontSize = 25 },
                new Label { Text = "💡 NOTA: Flujo MANUAL HABILITADO ✅ | Envío Automático API DGII ⏸️ PENDIENTE DE ACTIVACIÓN", TextColor = Color.FromArgb("#9A5A00") },
                envioAutomatico, avisoEnvioAutomatico,
                new Label { Text = "⚠️ ADVERTENCIA: Estas credenciales son para la Oficina Virtual DGII. No comparta este archivo con terceros. No se envían automáticamente al portal.", TextColor = Color.FromArgb("#9A5A00") },
                _rnc, _contrasena, _token,
                new Label { Text = "El token solo permanece en esta pantalla y no se guarda. Las credenciales guardadas utilizan el almacenamiento seguro del dispositivo." },
                guardar, borrar, _estadoCredenciales,
                _selectorModulo,
                _columnasModulos,
                new Label { Text = "El inicio de sesión, la subida y la confirmación de envío se realizan manualmente en la Oficina Virtual. No se hace scraping, login ni envío automático." },
                new Label { Text = "Los TXT generados por el programa son borradores internos, NO archivos oficiales. La validación oficial aún no está disponible. El inicio de sesión y la selección del archivo se realizan manualmente en la Oficina Virtual. NO se realiza scraping ni envío automático.", TextColor = Color.FromArgb("#9A5A00") }
            }
        }};
    }

    private string Clave => "dgii_606_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para utilizar esta pantalla."))));

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var version = ++_versionPantalla;
        try
        {
            var clave = Clave;
            var json = await SecureStorage.Default.GetAsync(clave);
            if (version != _versionPantalla || clave != Clave || json is null ||
                !string.IsNullOrWhiteSpace(_rnc.Text) || !string.IsNullOrWhiteSpace(_contrasena.Text)) return;
            var datos = JsonSerializer.Deserialize<Credenciales>(json) ?? throw new InvalidDataException("Credenciales guardadas inválidas.");
            _rnc.Text = datos.Rnc;
            _contrasena.Text = datos.Contrasena;
        }
        catch (Exception)
        {
            _logger.LogWarning("No se pudieron leer las credenciales DGII del almacenamiento seguro.");
            _resultado.TextColor = Color.FromArgb("#9A5A00");
            _resultado.Text = "No se pudieron leer las credenciales guardadas. Verifica el almacenamiento seguro del dispositivo.";
        }
    }

    protected override void OnDisappearing()
    {
        _versionPantalla++;
        _rnc.Text = string.Empty;
        _contrasena.Text = string.Empty;
        _token.Text = string.Empty;
        base.OnDisappearing();
    }

    private async void GuardarCredenciales(object? sender, EventArgs e)
    {
        if (sender is not Button boton) return;
        boton.IsEnabled = false;
        try
        {
            var rnc = (_rnc.Text ?? string.Empty).Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(rnc, @"^(?:[0-9]{9}|[0-9]{11})$") || string.IsNullOrWhiteSpace(_contrasena.Text))
            {
                _estadoCredenciales.Text = "Introduce un RNC de 9 u 11 dígitos y la contraseña.";
                return;
            }
            var clave = Clave;
            var datos = JsonSerializer.Serialize(new Credenciales(rnc, _contrasena.Text));
            await SecureStorage.Default.SetAsync(clave, datos);
            if (!string.Equals(await SecureStorage.Default.GetAsync(clave), datos, StringComparison.Ordinal))
                throw new InvalidOperationException("No se pudo verificar el almacenamiento seguro.");
            _estadoCredenciales.Text = "✅ RNC y contraseña guardados de forma segura para esta cuenta. El token no se guarda.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Falló el almacenamiento seguro de credenciales DGII. Tipo: {TipoError}", ex.GetType().Name);
            _estadoCredenciales.Text = "No se pudo confirmar el guardado seguro. Comprueba el almacenamiento seguro del dispositivo e inténtalo nuevamente.";
        }
        finally { boton.IsEnabled = true; }
    }

    private async void BorrarCredenciales(object? sender, EventArgs e)
    {
        try
        {
            var eliminadas = SecureStorage.Default.Remove(Clave);
            _contrasena.Text = _token.Text = string.Empty;
            await DisplayAlertAsync("Credenciales DGII", eliminadas ? "Credenciales guardadas eliminadas." : "No había credenciales guardadas para esta cuenta.", "Aceptar");
        }
        catch (Exception)
        {
            _logger.LogWarning("No se pudieron eliminar las credenciales DGII del almacenamiento seguro.");
            await DisplayAlertAsync("Credenciales DGII", "No se pudieron eliminar las credenciales guardadas.", "Aceptar");
        }
    }

    private async void OnModuloSeleccionado(object? sender, EventArgs e)
    {
        var destino = _selectorModulo.SelectedIndex == 1 ? _columna607 : _columna606;
        if (Content is ScrollView desplazamiento)
            await desplazamiento.ScrollToAsync(destino, ScrollToPosition.Start, true);
    }

    private void OnColumnasModulosSizeChanged(object? sender, EventArgs e)
    {
        var apilar = _columnasModulos.Width < 700;
        if (apilar == _columnasApiladas) return;
        _columnasApiladas = apilar;
        _columnasModulos.ColumnDefinitions.Clear();
        _columnasModulos.RowDefinitions.Clear();
        if (apilar)
        {
            _columnasModulos.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _columnasModulos.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _columnasModulos.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(_columna606, 0);
            Grid.SetRow(_columna606, 0);
            Grid.SetColumn(_columna607, 0);
            Grid.SetRow(_columna607, 1);
        }
        else
        {
            _columnasModulos.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _columnasModulos.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _columnasModulos.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(_columna606, 0);
            Grid.SetRow(_columna606, 0);
            Grid.SetColumn(_columna607, 1);
            Grid.SetRow(_columna607, 0);
        }
    }

    private bool? _columnasApiladas;

    private void Invalidar(string? tipo = null)
    {
        if (tipo is null or "606")
        {
            _abrirCarga.IsEnabled = false;
            _enviado606 = false;
        }
        if (tipo is null or "607")
        {
            _abrirCarga607.IsEnabled = false;
            _enviado607 = false;
        }
    }

    private async void GenerarBorrador(object? sender, EventArgs e)
    {
        if (sender is not Button boton) return;
        boton.IsEnabled = false;
        try
        {
            var usuario = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión antes de generar el archivo.");
            if (_periodo.Date is not DateTime periodo) throw new InvalidOperationException("Selecciona el período a declarar.");
#if WINDOWS
            var documentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documentos)) throw new InvalidOperationException("No se encontró la carpeta Documentos.");
            var carpeta = Path.Combine(documentos, "DGII Automática", "Archivos 606");
#else
            var carpeta = Path.Combine(FileSystem.AppDataDirectory, "Archivos 606");
#endif
            var resultado = await _formatoDgii.Generar606Async(usuario, (_rnc.Text ?? string.Empty).Trim(), periodo.Year, periodo.Month, carpeta);
            if (!resultado.Valido)
            {
                _resultado.TextColor = Color.FromArgb("#C62828");
                _resultado.Text = "⚠️ No se generó el archivo 606. Corrija los registros fiscales que se muestran a continuación:\n" + string.Join("\n", resultado.Errores);
                return;
            }
            _resultado.TextColor = Color.FromArgb("#60788F");
            _seleccion = new FileResult(resultado.Ruta);
            Invalidar("606");
            _archivo.Text = $"Archivo generado: {resultado.Ruta}";
            _resultado.Text = $"✅ Archivo generado; coincide con la estructura de columnas de la plantilla DGII 606 incluida ({resultado.CantidadRegistros} registros). Borrador local: confirme con la herramienta oficial DGII.";
        }
        catch (PlantillaDgiiException ex)
        {
            _logger.LogError(ex, "No se pudo leer la plantilla DGII 606.");
            _resultado.TextColor = Color.FromArgb("#C62828");
            _resultado.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo generar el borrador interno 606.");
            _resultado.TextColor = Color.FromArgb("#9A5A00");
            _resultado.Text = $"No se pudo generar el borrador interno: {ex.Message}";
        }
        finally { boton.IsEnabled = true; }
    }

    private async void GenerarBorrador607(object? sender, EventArgs e)
    {
        if (sender is not Button boton) return;
        boton.IsEnabled = false;
        try
        {
            var usuario = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión antes de generar el archivo.");
            var periodo = _periodo607.Date ?? throw new InvalidOperationException("Selecciona el período a declarar.");
#if WINDOWS
            var documentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documentos)) throw new InvalidOperationException("No se encontró la carpeta Documentos.");
            var carpeta = Path.Combine(documentos, "DGII Automática", "Archivos 607");
#else
            var carpeta = Path.Combine(FileSystem.AppDataDirectory, "Archivos 607");
#endif
            var resultado = await _formatoDgii.Generar607Async(usuario, (_rnc.Text ?? string.Empty).Trim(), periodo.Year, periodo.Month, carpeta);
            if (!resultado.Valido)
            {
                _resultado607.TextColor = Color.FromArgb("#C62828");
                _resultado607.Text = "⚠️ No se generó el archivo 607. Corrija los registros fiscales que se muestran a continuación:\n" + string.Join("\n", resultado.Errores);
                return;
            }
            _resultado607.TextColor = Color.FromArgb("#60788F");
            _seleccion607 = new FileResult(resultado.Ruta);
            Invalidar("607");
            _archivo607.Text = $"Archivo generado: {resultado.Ruta}";
            _resultado607.Text = $"✅ Archivo generado; coincide con la estructura de columnas de la plantilla DGII 607 incluida ({resultado.CantidadRegistros} registros). Borrador local: confirme con la herramienta oficial DGII.";
        }
        catch (PlantillaDgiiException ex)
        {
            _logger.LogError(ex, "No se pudo leer la plantilla DGII 607.");
            _resultado607.TextColor = Color.FromArgb("#C62828");
            _resultado607.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo generar el borrador estructurado 607.");
            _resultado607.TextColor = Color.FromArgb("#9A5A00");
            _resultado607.Text = $"No se pudo generar el archivo 607: {ex.Message}";
        }
        finally { boton.IsEnabled = true; }
    }

    private async Task AbrirCorreccionAsync(string tipo)
    {
        try
        {
            var usuario = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión antes de corregir registros fiscales.");
            var periodo = tipo == "606" ? _periodo.Date : _periodo607.Date;
            if (periodo is not DateTime mes) throw new InvalidOperationException("Selecciona el período a declarar.");
            var pagina = new CorreccionRegistrosFiscalesPage(_db, _formatoDgii, usuario, tipo, mes,
                (_rnc.Text ?? string.Empty).Trim(), resultado => RecibirArchivoCorregidoAsync(tipo, resultado));
            await Navigation.PushAsync(pagina);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la corrección de registros fiscales para el Formato {Tipo}.", tipo);
            await DisplayAlertAsync("Corrección fiscal", $"No se pudo abrir la pantalla de corrección: {ex.Message}", "Aceptar");
        }
    }

    private Task RecibirArchivoCorregidoAsync(string tipo, ResultadoExportacionFiscal resultado)
    {
        Invalidar(tipo);
        if (tipo == "606")
        {
            _seleccion = new FileResult(resultado.Ruta);
            _archivo.Text = $"Archivo generado: {resultado.Ruta}";
            _resultado.Text = $"Borrador interno 606 generado tras corregir y validar {resultado.CantidadRegistros} registros. No es un archivo oficial.";
        }
        else
        {
            _seleccion607 = new FileResult(resultado.Ruta);
            _archivo607.Text = $"Archivo generado: {resultado.Ruta}";
            _resultado607.Text = $"Borrador estructurado 607 generado tras corregir y validar {resultado.CantidadRegistros} registros. Confírmalo con la herramienta oficial DGII.";
        }
        return Task.CompletedTask;
    }

    private async void AbrirHerramienta(object? sender, EventArgs e) => await AbrirEnlaceHerramientaAsync();

    private async void AbrirHerramienta607(object? sender, EventArgs e) => await AbrirEnlaceHerramientaAsync();

    private async Task AbrirEnlaceHerramientaAsync()
    {
        try
        {
            if (!await Browser.Default.OpenAsync("https://dgii.gov.do/herramientas/formularios/formatoEnvioDatos/Paginas/default.aspx", BrowserLaunchMode.External))
                throw new InvalidOperationException("No se pudo abrir el navegador.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la herramienta oficial DGII.");
            await DisplayAlertAsync("Herramienta DGII", "No se pudo abrir la página de herramientas oficiales.", "Aceptar");
        }
    }

    private async void SeleccionarArchivo(object? sender, EventArgs e)
    {
        try
        {
            var seleccionado = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Selecciona el TXT 606" });
            if (seleccionado is null) return;
            _seleccion = seleccionado;
            Invalidar("606");
            _archivo.Text = $"Archivo: {seleccionado.FileName}";
            _resultado.Text = "Valida el archivo antes de continuar.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo seleccionar el archivo 606.");
            _resultado.Text = "No se pudo seleccionar el archivo 606.";
        }
    }

    private async void ValidarArchivo(object? sender, EventArgs e) => await ValidarArchivoAsync("606");

    private async void ValidarArchivo607(object? sender, EventArgs e) => await ValidarArchivoAsync("607");

    private async Task ValidarArchivoAsync(string tipo)
    {
        var archivo = tipo == "606" ? _seleccion : _seleccion607;
        var periodoSeleccionado = tipo == "606" ? _periodo.Date : _periodo607.Date;
        var estado = tipo == "606" ? _resultado : _resultado607;
        Invalidar(tipo);
        if (periodoSeleccionado is not DateTime periodo) { estado.Text = "Selecciona el período a declarar."; return; }
        if (archivo is null) { estado.Text = $"Selecciona primero un archivo TXT {tipo}."; return; }
        if (!string.Equals(Path.GetExtension(archivo.FileName), ".txt", StringComparison.OrdinalIgnoreCase)) { estado.Text = "El archivo debe ser TXT."; return; }
        try
        {
            var usuario = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión antes de revisar el archivo.");
            var pagina = new RevisionFinalDgiiPage(_db, _formatoDgii, usuario, tipo, periodo,
                (_rnc.Text ?? string.Empty).Trim(), archivo,
                resultado => RecibirArchivoCorregidoAsync(tipo, resultado),
                ActualizarEstadoRevisionAsync, AbrirConArchivoAsync);
            await Navigation.PushModalAsync(new NavigationPage(pagina));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la revisión final del archivo {Tipo}.", tipo);
            estado.Text = $"No se pudo abrir la revisión final del archivo {tipo}: {ex.Message}";
        }
    }

    private Task ActualizarEstadoRevisionAsync(string tipo, FileResult archivo, bool valido, string mensaje)
    {
        if (tipo == "606")
        {
            _seleccion = archivo;
            _archivo.Text = $"Archivo: {archivo.FileName}";
            _resultado.TextColor = valido ? Color.FromArgb("#60788F") : Color.FromArgb("#C62828");
            _resultado.Text = mensaje;
            _abrirCarga.IsEnabled = valido;
        }
        else
        {
            _seleccion607 = archivo;
            _archivo607.Text = $"Archivo: {archivo.FileName}";
            _resultado607.TextColor = valido ? Color.FromArgb("#60788F") : Color.FromArgb("#C62828");
            _resultado607.Text = mensaje;
            _abrirCarga607.IsEnabled = valido;
        }
        return Task.CompletedTask;
    }

    private async void SeleccionarArchivo607(object? sender, EventArgs e)
    {
        try
        {
            var seleccionado = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Selecciona el TXT 607" });
            if (seleccionado is null) return;
            _seleccion607 = seleccionado;
            Invalidar("607");
            _archivo607.Text = $"Archivo: {seleccionado.FileName}";
            _resultado607.Text = "Valida el archivo antes de continuar.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo seleccionar el archivo 607.");
            _resultado607.Text = "No se pudo seleccionar el archivo 607.";
        }
    }

    private async void AbrirConArchivo(object? sender, EventArgs e)
    {
        if (_seleccion is null || !_abrirCarga.IsEnabled) return;
        await AbrirConArchivoAsync("606", _seleccion);
    }

    private async void AbrirConArchivo607(object? sender, EventArgs e)
    {
        if (_seleccion607 is null || !_abrirCarga607.IsEnabled) return;
        await AbrirConArchivoAsync("607", _seleccion607);
    }

    private async Task AbrirConArchivoAsync(string tipo, FileResult archivo)
    {
        try
        {
            var fecha = tipo == "606" ? _periodo.Date : _periodo607.Date;
            if (fecha is not DateTime periodo) throw new InvalidOperationException("Selecciona el período a declarar.");
            await using (var stream = await archivo.OpenReadAsync())
            {
                var validacion = await FormatoEnvioDgiiService.ValidarArchivoAsync(stream, tipo,
                    (_rnc.Text ?? string.Empty).Trim(), periodo.Year, periodo.Month);
                if (!validacion.Valido)
                {
                    Invalidar(tipo);
                    throw new InvalidDataException("El archivo ya no supera la validación local. Revísalo nuevamente antes de abrir la Oficina Virtual.");
                }
            }
            if (File.Exists(archivo.FullPath)) await Clipboard.Default.SetTextAsync(archivo.FullPath);
            if (!await Browser.Default.OpenAsync("https://dgii.gov.do/ofv/login.aspx", BrowserLaunchMode.External))
                throw new InvalidOperationException("No se pudo abrir el navegador.");
            var instruccion = tipo == "606"
                ? "Ruta copiada cuando el dispositivo permite acceder a ella. En la Oficina Virtual, diríjase a Formatos de Envíos → Enviar Archivos → Seleccione Formato 606 y adjunte manualmente el TXT revisado."
                : "Ruta copiada cuando el dispositivo permite acceder a ella. En la Oficina Virtual, diríjase a Formatos de Envíos → Enviar Archivos → Seleccione Formato 607 y adjunte manualmente el TXT revisado.";
            await DisplayAlertAsync("Carga manual DGII", instruccion, "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la Oficina Virtual para el Formato {Tipo}.", tipo);
            await DisplayAlertAsync("Oficina Virtual DGII", $"No se pudo abrir la carga manual: {ex.Message}", "Aceptar");
        }
    }

    private async void AbrirPortal(object? sender, EventArgs e) => await AbrirPortalAsync();

    private async void AbrirPortal607(object? sender, EventArgs e) => await AbrirPortalAsync();

    private async Task AbrirPortalAsync()
    {
        try
        {
            if (!await Browser.Default.OpenAsync("https://dgii.gov.do/ofv/login.aspx", BrowserLaunchMode.External)) throw new InvalidOperationException("No se pudo abrir el navegador.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la Oficina Virtual DGII.");
            await DisplayAlertAsync("Oficina Virtual DGII", "No se pudo abrir el navegador.", "Aceptar");
        }
    }

    private async void MarcarComoEnviadoAsync(string tipo)
    {
        var archivo = tipo == "606" ? _seleccion : _seleccion607;
        var periodoSeleccionado = tipo == "606" ? _periodo.Date : _periodo607.Date;
        var estado = tipo == "606" ? _resultado : _resultado607;
        var botonAbrir = tipo == "606" ? _abrirCarga : _abrirCarga607;
        if (tipo == "606" ? _enviado606 : _enviado607) return;
        if (periodoSeleccionado is not DateTime periodo) { estado.Text = "Selecciona el período a declarar."; return; }
        if (archivo is null || !botonAbrir.IsEnabled)
        {
            estado.Text = $"Valida primero el archivo {tipo} antes de marcarlo como enviado.";
            return;
        }
        if (!await DisplayAlertAsync($"Confirmar envío {tipo}", "Confirma únicamente si completaste manualmente el envío en la Oficina Virtual DGII y recibiste el acuse. Esta acción guardará una copia local del archivo.", "Sí, confirmar", "Cancelar")) return;

        try
        {
            var usuario = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión antes de archivar el envío.");
            ResultadoValidacionFormatoDgii resultado;
            await using (var verificacion = await archivo.OpenReadAsync())
                resultado = await FormatoEnvioDgiiService.ValidarArchivoAsync(verificacion, tipo, (_rnc.Text ?? string.Empty).Trim(), periodo.Year, periodo.Month);
            if (!resultado.Valido)
            {
                Invalidar(tipo);
                estado.Text = "El archivo ya no supera la validación local; no se archivó como enviado.";
                return;
            }
#if WINDOWS
            var documentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documentos)) throw new InvalidOperationException("No se encontró la carpeta Documentos.");
            var carpeta = Path.Combine(documentos, "DGII Automática", $"ARCHIVOS_DGII_ENVIADOS_{tipo}");
#else
            var carpeta = Path.Combine(FileSystem.AppDataDirectory, $"ARCHIVOS_DGII_ENVIADOS_{tipo}");
#endif
            await using var contenido = await archivo.OpenReadAsync();
            var cantidad = resultado.CantidadRegistros;
            var ruta = await _formatoDgii.CrearCopiaEnviadaAsync(usuario, tipo, periodo.Year, periodo.Month, (_rnc.Text ?? string.Empty).Trim(), contenido, carpeta, archivo.FileName, cantidad);
            if (tipo == "606") _enviado606 = true;
            else _enviado607 = true;
            botonAbrir.IsEnabled = false;
            estado.Text = $"✅ Copia de respaldo {tipo} creada: {ruta}\nSe guardó el historial de confirmación manual; la aplicación no recibe confirmación directa de la DGII.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo archivar el Formato {Tipo} como enviado.", tipo);
            estado.Text = $"No se pudo crear el respaldo ni registrar el envío {tipo}: {ex.Message}";
        }
    }
}
