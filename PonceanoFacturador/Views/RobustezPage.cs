using System.Globalization;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public sealed class RobustezPage : ContentPage
{
    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly ExportacionFiscalPreliminarService _exportador;
    private readonly RncContribuyentesService _rncLocal;
    private readonly IServiceProvider _servicios;
    private readonly ILogger<RobustezPage> _logger;
    private readonly Picker _tipo = new() { Title = "Reporte fiscal" };
    private readonly DatePicker _periodo = new() { Date = DateTime.Today };
    private readonly CheckBox _incluirNotas = new();
    private readonly Label _estado = new() { TextColor = Color.FromArgb("#60788F") };
    private readonly VerticalStackLayout _historial = new() { Spacing = 6 };
    private readonly VerticalStackLayout _bitacora = new() { Spacing = 6 };
    private readonly VerticalStackLayout _alertas = new() { Spacing = 6 };

    public RobustezPage(DatabaseService db, AuthService auth, ExportacionFiscalPreliminarService exportador, RncContribuyentesService rncLocal, IServiceProvider servicios, ILogger<RobustezPage> logger)
    {
        _db = db;
        _auth = auth;
        _exportador = exportador;
        _rncLocal = rncLocal;
        _servicios = servicios;
        _logger = logger;
        Title = "Robustez y mantenimiento";
        _tipo.ItemsSource = new[] { "606 — Compras (borrador interno)", "607 — Ventas (borrador interno)" };
        _tipo.SelectedIndex = 0;
        var exportar = new Button { Text = "Validar y generar TXT preliminar", BackgroundColor = Color.FromArgb("#467FA6"), TextColor = Colors.White };
        exportar.Clicked += OnExportarClicked;
        var consultar = new Button { Text = "Actualizar información del sistema", BackgroundColor = Color.FromArgb("#F1F1F1"), TextColor = Color.FromArgb("#555555") };
        consultar.Clicked += OnInformacionClicked;
        var optimizar = new Button { Text = "Optimizar base de datos", BackgroundColor = Color.FromArgb("#F1F1F1"), TextColor = Color.FromArgb("#555555") };
        optimizar.Clicked += OnOptimizarClicked;
        var cierrePeriodo = BotonModulo("Cierre y reapertura de período");
        var roles = BotonModulo("Roles y autorizaciones de administrador");
        var verificarRnc = BotonModulo("Verificación de RNC en DGII");
        var devoluciones = BotonModulo("Devoluciones parciales de inventario");
        var respaldos = BotonModulo("Archivo histórico y respaldos automáticos");
        var alertasAvanzadas = BotonModulo("Alertas avanzadas configurables");
        var exportacion = new Button { Text = "Abrir módulo 606/607 (generación y envío manual)" };
        exportacion.Clicked += AbrirEnvioFiscal;
        var ncf = new Button { Text = "Series NCF y rangos disponibles" };
        ncf.Clicked += AbrirSeriesNcf;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24, Spacing = 12, MaximumWidthRequest = 850,
                Children =
                {
                    new Label { Text = "Robustez y mantenimiento", FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#667D91") },
                    new Label { Text = "La exportación es preliminar y no se debe presentar a DGII sin convertirla y validarla contra el diseño oficial vigente. La autenticación no proporciona roles confiables: la reapertura, autorización de diferencias y anulación fiscal quedan bloqueadas; el cierre sí queda auditado.", TextColor = Color.FromArgb("#9A5A00") },
                    _tipo, _periodo,
                    new HorizontalStackLayout { Children = { _incluirNotas, new Label { Text = "Incluir notas relacionadas", VerticalOptions = LayoutOptions.Center } } },
                    exportar,
                    new Label { Text = "Integridad y mantenimiento local", FontSize = 18, FontAttributes = FontAttributes.Bold },
                    consultar, optimizar, _estado,
                    new Label { Text = "Alertas de reposición", FontSize = 18, FontAttributes = FontAttributes.Bold }, _alertas,
                    new Label { Text = "Bitácora de cierres y cambios", FontSize = 18, FontAttributes = FontAttributes.Bold }, _bitacora,
                    new Label { Text = "Historial de exportaciones preliminares", FontSize = 18, FontAttributes = FontAttributes.Bold }, _historial,
                    new Label { Text = "MÓDULOS DISPONIBLES Y RESTRICCIONES DE SEGURIDAD", FontSize = 18, FontAttributes = FontAttributes.Bold },
                    cierrePeriodo, roles, verificarRnc, exportacion, devoluciones, respaldos, ncf, alertasAvanzadas
                }
            }
        };
    }

    private Button BotonModulo(string nombre)
    {
        var boton = new Button { Text = nombre };
        boton.Clicked += async (_, _) => await Navigation.PushAsync(new ModuloAvanzadoPage(nombre, _db, _auth, _rncLocal, _servicios.GetRequiredService<ILogger<ModuloAvanzadoPage>>()));
        return boton;
    }

    private async void AbrirEnvioFiscal(object? sender, EventArgs e)
    {
        try
        {
            var pagina = _servicios.GetRequiredService<Envio606Page>();
            await Navigation.PushAsync(pagina);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la preparación de libros DGII 606/607.");
            _estado.Text = "No se pudo abrir el módulo 606/607.";
        }
    }

    private async void AbrirSeriesNcf(object? sender, EventArgs e)
    {
        try
        {
            var usuario = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para administrar las series NCF.");
            await Navigation.PushModalAsync(new NcfConfiguracionPage(_db, usuario, _servicios.GetRequiredService<ILogger<NcfConfiguracionPage>>()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la gestión de series NCF.");
            _estado.Text = ex.Message;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        try
        {
            _alertas.Children.Clear();
            var bajos = await _db.GetArticulosBajoMinimoAsync();
            if (bajos.Count == 0) _alertas.Children.Add(new Label { Text = "No hay artículos bajo el mínimo configurado." });
            else foreach (var articulo in bajos)
                _alertas.Children.Add(new Label { Text = $"⚠ {articulo.Descripcion}: stock {articulo.Stock}, mínimo {articulo.StockMinimo}." });
            _bitacora.Children.Clear();
            var uid = _auth.CurrentUser?.IdFirebase;
            if (!string.IsNullOrWhiteSpace(uid))
            {
                var eventos = await _db.ObtenerBitacoraAuditoriaAsync(uid, 30);
                if (eventos.Count == 0) _bitacora.Children.Add(new Label { Text = "No hay eventos auditados." });
                foreach (var evento in eventos)
                    _bitacora.Children.Add(new Label { Text = $"{evento.FechaHora.ToLocalTime():dd/MM/yyyy HH:mm} · {evento.Accion} · {evento.Detalle}" });
            }
            await CargarHistorialAsync();
            await CargarInformacionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo cargar el módulo de robustez.");
            _estado.Text = "No se pudo cargar la información del sistema.";
        }
    }

    private async Task CargarInformacionAsync()
    {
        var (tamano, integridad) = await _db.ObtenerInformacionBaseDatosAsync();
        _estado.Text = $"Versión de aplicación: {AppInfo.Current.VersionString} · Base de datos local: {tamano / 1024d:N1} KB · Integridad SQLite: {integridad}.\nRespaldo diario: se intenta al primer inicio autenticado del día; la restauración directa no está habilitada.";
    }

    private async Task CargarHistorialAsync()
    {
        _historial.Children.Clear();
        var uid = _auth.CurrentUser?.IdFirebase;
        if (string.IsNullOrWhiteSpace(uid)) return;
        var exportaciones = await _db.ObtenerHistorialExportacionesDGIIAsync(uid);
        if (exportaciones.Count == 0) _historial.Children.Add(new Label { Text = "Todavía no hay exportaciones." });
        foreach (var exportacion in exportaciones.Take(20))
            _historial.Children.Add(new Label { Text = $"{exportacion.TipoReporte} · {exportacion.PeriodoMes:D2}/{exportacion.PeriodoAnio} · {exportacion.CantidadRegistros} registros · {exportacion.FechaHora.ToLocalTime():dd/MM/yyyy HH:mm} · {exportacion.Estado}" });
    }

    private async void OnExportarClicked(object? sender, EventArgs e)
    {
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión antes de exportar.");
            var tipo = _tipo.SelectedIndex == 0 ? "606" : "607";
            var fechaPeriodo = _periodo.Date ?? DateTime.Today;
            var resultado = await _exportador.GenerarAsync(uid, tipo, fechaPeriodo.Year, fechaPeriodo.Month, _incluirNotas.IsChecked);
            if (!resultado.Valido)
            {
                _estado.Text = $"No se generó el archivo. Corrige estos datos ({resultado.Errores.Count}):\n{string.Join("\n", resultado.Errores.Take(20))}";
                return;
            }
            await Share.Default.RequestAsync(new ShareFileRequest($"Borrador interno {tipo}", new ShareFile(resultado.Ruta)));
            _estado.Text = $"TXT preliminar generado: {resultado.CantidadRegistros} registros. No está certificado como archivo oficial DGII.";
            await CargarHistorialAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo generar la exportación fiscal preliminar.");
            await DisplayAlertAsync("Exportación fiscal", ex.Message, "Aceptar");
        }
    }

    private async void OnInformacionClicked(object? sender, EventArgs e)
    {
        try { await CargarInformacionAsync(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo consultar la información de la base de datos.");
            await DisplayAlertAsync("Mantenimiento", ex.Message, "Aceptar");
        }
    }

    private async void OnOptimizarClicked(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Optimizar base de datos", "Se reorganizará la base SQLite local. Asegúrate de tener un respaldo externo reciente antes de continuar.", "Optimizar", "Cancelar")) return;
        try
        {
            await _db.OptimizarBaseDatosAsync();
            await CargarInformacionAsync();
            await DisplayAlertAsync("Mantenimiento", "La optimización terminó correctamente.", "Aceptar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo optimizar la base de datos.");
            await DisplayAlertAsync("Mantenimiento", ex.Message, "Aceptar");
        }
    }
}
