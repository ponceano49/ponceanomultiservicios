using System.Globalization;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;
using SQLite;

namespace PonceanoFacturador.Views;

public sealed class ModuloAvanzadoPage : ContentPage
{
    private readonly string _modulo;
    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly RncContribuyentesService _rncLocal;
    private readonly ILogger<ModuloAvanzadoPage> _logger;
    private readonly Label _estado = new() { TextColor = Color.FromArgb("#60788F") };
    private readonly DatePicker _periodo = new() { Date = DateTime.Today, Format = "MM/yyyy" };
    private readonly Entry _busquedaRnc = new() { Placeholder = "RNC/cédula o razón social" };
    private readonly DatePicker _fechaDevolucion = new() { Date = DateTime.Today };
    private readonly Picker _direccion = new() { Title = "Tipo de devolución" };
    private readonly Entry _contraparte = new() { Placeholder = "Cliente o proveedor" };
    private readonly Picker _articulo = new() { Title = "Artículo" };
    private readonly Entry _cantidad = new() { Placeholder = "Cantidad", Keyboard = Keyboard.Numeric };
    private readonly Entry _motivo = new() { Placeholder = "Motivo de devolución" };
    private readonly VerticalStackLayout _alertas = new() { Spacing = 8 };
    private readonly List<(ConfiguracionAlerta Config, CheckBox Activa, DatePicker Fecha, TimePicker Hora)> _controlesAlertas = new();
    private List<Articulo> _articulos = new();

    public ModuloAvanzadoPage(string modulo, DatabaseService db, AuthService auth, RncContribuyentesService rncLocal, ILogger<ModuloAvanzadoPage> logger)
    {
        _modulo = modulo;
        _db = db;
        _auth = auth;
        _rncLocal = rncLocal;
        _logger = logger;
        Title = modulo;
        _periodo.DateSelected += async (_, _) => await CargarPeriodoAsync();
        Content = new ScrollView { Content = CrearContenido() };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_modulo == "Cierre y reapertura de período") await CargarPeriodoAsync();
        else if (_modulo == "Devoluciones parciales de inventario") await CargarArticulosAsync();
        else if (_modulo == "Alertas avanzadas configurables") await CargarAlertasAsync();
    }

    private View CrearContenido()
    {
        var contenido = new VerticalStackLayout { Padding = 24, Spacing = 12, MaximumWidthRequest = 800 };
        contenido.Children.Add(new Label { Text = _modulo, FontSize = 25, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#667D91") });
        switch (_modulo)
        {
            case "Cierre y reapertura de período":
                contenido.Children.Add(new Label { Text = "Cerrar un período impide registrar o modificar facturas y compras nuevas dentro de ese mes. La reapertura requiere una autorización de administrador verificada." });
                contenido.Children.Add(_periodo);
                var cerrar = new Button { Text = "✅ Cerrar Período" };
                cerrar.Clicked += CerrarPeriodo;
                var reabrir = new Button { Text = "🔓 Reabrir Período" };
                reabrir.Clicked += ReabrirPeriodo;
                contenido.Children.Add(cerrar);
                contenido.Children.Add(reabrir);
                contenido.Children.Add(new Label { Text = "No existe un proveedor de roles de administrador confiable configurado en esta instalación; la reapertura queda bloqueada. No se acepta un rol guardado localmente como autorización.", TextColor = Color.FromArgb("#9A5A00") });
                break;
            case "Roles y autorizaciones de administrador":
                contenido.Children.Add(new Label { Text = "La aplicación autentica cuentas con Firebase, pero no dispone de Firebase Admin SDK ni de custom claims/roles confiables. Por seguridad, esta pantalla no permite asignar roles ni contraseñas: hacerlo localmente permitiría elevar privilegios desde el propio dispositivo.", TextColor = Color.FromArgb("#9A5A00") });
                contenido.Children.Add(new Label { Text = "Para habilitar administración de usuarios se requiere configurar un servicio de backend autorizado que asigne roles verificables y registre auditoría remota." });
                break;
            case "Verificación de RNC en DGII":
                contenido.Children.Add(new Label { Text = "Consulta exclusivamente el padrón local descargado. No se consulta DGII ni se determina estado activo/inactivo sin una API oficial." });
                contenido.Children.Add(_busquedaRnc);
                var verificar = new Button { Text = "🔍 Verificar RNC" };
                verificar.Clicked += VerificarRnc;
                contenido.Children.Add(verificar);
                break;
            case "Devoluciones parciales de inventario":
                _direccion.ItemsSource = new[] { "Devolución de cliente (aumenta stock)", "Devolución a proveedor (reduce stock)" };
                _direccion.SelectedIndex = 0;
                contenido.Children.Add(new Label { Text = "Una devolución a proveedor no puede reducir el inventario por debajo de cero. Cada movimiento queda en el historial del artículo y en auditoría." });
                contenido.Children.Add(_fechaDevolucion);
                contenido.Children.Add(_direccion);
                contenido.Children.Add(_contraparte);
                contenido.Children.Add(_articulo);
                contenido.Children.Add(_cantidad);
                contenido.Children.Add(_motivo);
                var registrar = new Button { Text = "Registrar devolución y actualizar inventario" };
                registrar.Clicked += RegistrarDevolucion;
                contenido.Children.Add(registrar);
                break;
            case "Archivo histórico y respaldos automáticos":
                contenido.Children.Add(new Label { Text = "El respaldo manual usa VACUUM INTO de SQLite para crear una copia consistente. Además, se intenta un respaldo local automático en el primer inicio autenticado de cada día." });
                var respaldo = new Button { Text = "💾 Hacer Respaldo Manual" };
                respaldo.Clicked += CrearRespaldo;
                var restaurar = new Button { Text = "📂 Seleccionar y validar respaldo" };
                restaurar.Clicked += ValidarRespaldo;
                contenido.Children.Add(respaldo);
                contenido.Children.Add(restaurar);
                contenido.Children.Add(new Label { Text = "La restauración directa no está habilitada: reemplazar la base activa sin cerrar y validar la conexión podría corromper datos. La selección solo verifica integridad; conserva siempre una copia externa antes de restaurar.", TextColor = Color.FromArgb("#9A5A00") });
                break;
            case "Series NCF A02/A03":
                contenido.Children.Add(new Label { Text = "La pantalla de comprobantes permite consultar y editar rangos A02/A03, creados inactivos. La emisión de facturas continúa limitada a los tipos vigentes que ya admite el formulario; A02/A03 no se emitirán hasta verificar su clasificación/cadencia oficial y autorización DGII." });
                var abrirNcf = new Button { Text = "Abrir gestión de series NCF disponibles" };
                abrirNcf.Clicked += AbrirNcf;
                contenido.Children.Add(abrirNcf);
                break;
            case "Alertas avanzadas configurables":
                contenido.Children.Add(new Label { Text = "Configura preferencias y hora para cada alerta. Se muestran al abrir este módulo; la programación de notificaciones del sistema operativo no está instalada." });
                contenido.Children.Add(_alertas);
                var guardarAlertas = new Button { Text = "Guardar configuración de alertas" };
                guardarAlertas.Clicked += GuardarAlertas;
                contenido.Children.Add(guardarAlertas);
                break;
            default:
                contenido.Children.Add(new Label { Text = "Este módulo no está disponible." });
                break;
        }
        contenido.Children.Add(_estado);
        return contenido;
    }

    private string UsuarioId => _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para utilizar este módulo.");

    private async Task CargarPeriodoAsync()
    {
        try
        {
            var fecha = _periodo.Date ?? DateTime.Today;
            var periodo = await _db.ObtenerPeriodoFiscalAsync(UsuarioId, fecha.Year, fecha.Month);
            _estado.Text = periodo?.Estado == "C" ? $"Período {fecha:MM/yyyy}: CERRADO." : $"Período {fecha:MM/yyyy}: ABIERTO.";
        }
        catch (Exception ex) { MostrarError("No se pudo consultar el período fiscal.", ex); }
    }

    private async void CerrarPeriodo(object? sender, EventArgs e)
    {
        try
        {
            var fecha = _periodo.Date ?? DateTime.Today;
            await _db.CerrarPeriodoFiscalAsync(UsuarioId, fecha.Year, fecha.Month, _auth.CurrentUser?.Nombre ?? string.Empty);
            _estado.Text = $"✅ PERÍODO CERRADO: {fecha:MM/yyyy} — No se podrán modificar registros para este período.";
        }
        catch (Exception ex) { MostrarError(ex.Message, ex); }
    }

    private async void ReabrirPeriodo(object? sender, EventArgs e)
    {
        await DisplayAlertAsync("Reapertura protegida", "No hay una autorización de administrador verificable configurada. El período no se reabrió.", "Aceptar");
    }

    private async void VerificarRnc(object? sender, EventArgs e)
    {
        try
        {
            var valor = _busquedaRnc.Text?.Trim() ?? string.Empty;
            var rnc = ListaLocalRncService.NormalizarRnc(valor);
            string? razon;
            string? resultado;
            if (ListaLocalRncService.EsRncValido(rnc))
            {
                razon = await _rncLocal.BuscarPorRncAsync(rnc);
                resultado = razon is null ? "⚠️ RNC NO ENCONTRADO en el padrón local. No se consultó la DGII." : $"✅ RNC encontrado en el padrón local: {rnc} — {razon}. Esto no confirma estado activo.";
            }
            else
            {
                var coincidencias = await _rncLocal.BuscarPorRazonSocialAsync(valor);
                resultado = coincidencias.Count == 0 ? "⚠️ Razón social no encontrada en el padrón local." : "Coincidencias locales:\n" + string.Join("\n", coincidencias.Select(item => $"{item.Rnc} — {item.RazonSocial}"));
                rnc = coincidencias.Count == 1 ? coincidencias[0].Rnc : string.Empty;
            }
            await _db.RegistrarEventoAuditoriaAsync(UsuarioId, "Verificación de RNC local", "RNC", rnc, resultado);
            _estado.Text = resultado;
        }
        catch (Exception ex) { MostrarError("No se pudo consultar el padrón local de RNC.", ex); }
    }

    private async Task CargarArticulosAsync()
    {
        try
        {
            _articulos = await _db.GetArticulosAsync();
            _articulo.ItemsSource = _articulos.Select(item => $"{item.Codigo} · {item.Descripcion} (stock {item.Stock})").ToList();
            _articulo.SelectedIndex = _articulos.Count > 0 ? 0 : -1;
            _estado.Text = _articulos.Count == 0 ? "No hay artículos registrados." : string.Empty;
        }
        catch (Exception ex) { MostrarError("No se pudieron cargar los artículos.", ex); }
    }

    private async void RegistrarDevolucion(object? sender, EventArgs e)
    {
        try
        {
            if (_articulo.SelectedIndex < 0 || _articulo.SelectedIndex >= _articulos.Count) throw new InvalidOperationException("Selecciona un artículo.");
            if (!int.TryParse(_cantidad.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var cantidad) || cantidad <= 0) throw new InvalidOperationException("La cantidad debe ser un entero positivo.");
            var fecha = _fechaDevolucion.Date ?? DateTime.Today;
            var devolucion = new DevolucionInventario
            {
                UsuarioId = UsuarioId, Fecha = DateTime.SpecifyKind(fecha.Date, DateTimeKind.Local).ToUniversalTime(),
                Direccion = _direccion.SelectedIndex == 0 ? "Cliente" : "Proveedor", Contraparte = _contraparte.Text?.Trim() ?? string.Empty,
                ArticuloId = _articulos[_articulo.SelectedIndex].Id, Cantidad = cantidad, Motivo = _motivo.Text?.Trim() ?? string.Empty
            };
            await _db.RegistrarDevolucionInventarioAsync(devolucion);
            _estado.Text = $"✅ Devolución registrada. Inventario actualizado para {devolucion.Cantidad} × {devolucion.Articulo}.";
            _cantidad.Text = _motivo.Text = _contraparte.Text = string.Empty;
            await CargarArticulosAsync();
        }
        catch (Exception ex) { MostrarError(ex.Message, ex); }
    }

    private async void CrearRespaldo(object? sender, EventArgs e)
    {
        try
        {
#if WINDOWS
            var documentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documentos)) throw new InvalidOperationException("No se encontró la carpeta Documentos.");
            var carpeta = Path.Combine(documentos, "PonceanoFacturador", "Respaldos");
#else
            var carpeta = Path.Combine(FileSystem.AppDataDirectory, "Respaldos");
#endif
            var ruta = await _db.CrearRespaldoBaseDatosAsync(UsuarioId, carpeta);
            _estado.Text = $"✅ RESPALDO CREADO CORRECTAMENTE en {ruta}";
        }
        catch (Exception ex) { MostrarError("No se pudo crear el respaldo.", ex); }
    }

    private async void ValidarRespaldo(object? sender, EventArgs e)
    {
        try
        {
            var archivo = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Selecciona una copia SQLite de PonceanoFacturador" });
            if (archivo is null) return;
            var extension = Path.GetExtension(archivo.FileName);
            if (!string.Equals(extension, ".db3", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".sqlite", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("El archivo seleccionado no tiene extensión .db3 o .sqlite.");
            var rutaTemporal = Path.Combine(FileSystem.CacheDirectory, $"validar-respaldo-{Guid.NewGuid():N}{extension}");
            try
            {
                await using (var origen = await archivo.OpenReadAsync())
                await using (var destino = new FileStream(rutaTemporal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await origen.CopyToAsync(destino);
                using var conexion = new SQLiteConnection(rutaTemporal, SQLiteOpenFlags.ReadOnly);
                var resultado = conexion.ExecuteScalar<string>("PRAGMA integrity_check");
                if (!string.Equals(resultado, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"La integridad SQLite falló: {resultado}");
            }
            finally { if (File.Exists(rutaTemporal)) File.Delete(rutaTemporal); }
            _estado.Text = "✅ El respaldo seleccionado pasó la comprobación de integridad. No se restauró ni reemplazó la base activa.";
            await _db.RegistrarEventoAuditoriaAsync(UsuarioId, "Validación de respaldo", "BaseDatos", archivo.FileName, "El respaldo pasó PRAGMA integrity_check; no se restauró.");
        }
        catch (Exception ex) { MostrarError("No se pudo validar el respaldo seleccionado.", ex); }
    }

    private async void AbrirNcf(object? sender, EventArgs e)
    {
        try
        {
            var logger = Application.Current?.Handler?.MauiContext?.Services.GetService<ILogger<NcfConfiguracionPage>>();
            if (logger is null) throw new InvalidOperationException("No se pudo cargar el servicio de configuración NCF.");
            await Navigation.PushModalAsync(new NcfConfiguracionPage(_db, UsuarioId, logger));
        }
        catch (Exception ex) { MostrarError("No se pudo abrir la gestión NCF.", ex); }
    }

    private async Task CargarAlertasAsync()
    {
        try
        {
            var usuario = UsuarioId;
            var existentes = await _db.ObtenerConfiguracionesAlertasAsync(usuario);
            var tipos = new[] { "Vencimiento DGII", "Stock bajo", "Serie NCF por agotarse", "Facturas pendientes" };
            var mensaje = new[] { "Vencimiento para enviar 606/607", "Stock bajo en inventario", "Serie NCF por agotarse", "Facturas pendientes por validar" };
            _alertas.Children.Clear();
            _controlesAlertas.Clear();
            for (var index = 0; index < tipos.Length; index++)
            {
                var config = existentes.FirstOrDefault(item => item.TipoAlerta == tipos[index]) ?? new ConfiguracionAlerta { UsuarioId = usuario, TipoAlerta = tipos[index], Condicion = tipos[index] };
                var activa = new CheckBox { IsChecked = config.Activa };
                var fecha = DateTime.TryParseExact(config.FechaAviso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var valorFecha) ? valorFecha : DateTime.Today;
                var fechaSelector = new DatePicker { Date = fecha, Format = "dd/MM/yyyy" };
                var hora = TimeOnly.TryParse(config.HoraAviso, CultureInfo.InvariantCulture, out var valorHora) ? valorHora.ToTimeSpan() : new TimeSpan(9, 0, 0);
                var selector = new TimePicker { Time = hora, Format = "HH:mm" };
                var fila = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) },
                    ColumnSpacing = 8
                };
                fila.Add(activa, 0);
                fila.Add(new Label { Text = mensaje[index], VerticalOptions = LayoutOptions.Center }, 1);
                fila.Add(fechaSelector, 2);
                fila.Add(selector, 3);
                _alertas.Children.Add(fila);
                _controlesAlertas.Add((config, activa, fechaSelector, selector));
            }
        }
        catch (Exception ex) { MostrarError("No se pudieron cargar las alertas.", ex); }
    }

    private async void GuardarAlertas(object? sender, EventArgs e)
    {
        try
        {
            foreach (var (config, activa, fecha, hora) in _controlesAlertas)
            {
                config.Activa = activa.IsChecked;
                config.FechaAviso = (fecha.Date ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                config.HoraAviso = TimeOnly.FromTimeSpan(hora.Time ?? TimeSpan.Zero).ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            await _db.GuardarConfiguracionesAlertasAsync(UsuarioId, _controlesAlertas.Select(control => control.Config).ToArray());
            _estado.Text = "✅ Preferencias y horarios de alertas guardados localmente. Las alertas se revisan al abrir el módulo; no se programan notificaciones del sistema.";
        }
        catch (Exception ex) { MostrarError("No se pudieron guardar las alertas.", ex); }
    }

    private void MostrarError(string mensaje, Exception ex)
    {
        _logger.LogError(ex, "Error en el módulo avanzado {Modulo}.", _modulo);
        _estado.Text = $"{mensaje} {ex.Message}";
    }
}
