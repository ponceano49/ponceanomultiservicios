using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public sealed class TurnosPage : ContentPage
{
    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly ImpresionWindowsService _impresion;
    private readonly ILogger<TurnosPage> _logger;
    private readonly Label _estado = new() { TextColor = Color.FromArgb("#60788F") };
    private readonly VerticalStackLayout _historial = new() { Spacing = 8 };

    public TurnosPage(DatabaseService db, AuthService auth, ImpresionWindowsService impresion, ILogger<TurnosPage> logger)
    {
        _db = db;
        _auth = auth;
        _impresion = impresion;
        _logger = logger;
        Title = "Turnos y cierre de caja";
        var abrir = new Button { Text = "Abrir turno actual", BackgroundColor = Color.FromArgb("#467FA6"), TextColor = Colors.White };
        abrir.Clicked += OnAbrirClicked;
        var cerrar = new Button { Text = "Cerrar turno y generar reporte", BackgroundColor = Color.FromArgb("#55B85B"), TextColor = Colors.White };
        cerrar.Clicked += OnCerrarClicked;
        var actualizar = new Button { Text = "Actualizar", BackgroundColor = Color.FromArgb("#F1F1F1"), TextColor = Color.FromArgb("#555555") };
        actualizar.Clicked += async (_, _) => await CargarAsync();
        var impresora = new Button { Text = "Seleccionar impresora para reportes", BackgroundColor = Color.FromArgb("#F1F1F1"), TextColor = Color.FromArgb("#555555") };
        impresora.Clicked += OnSeleccionarImpresoraClicked;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24, Spacing = 14, MaximumWidthRequest = 850,
                Children =
                {
                    new Label { Text = "Turnos y cierre de caja", FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#667D91") },
                    new Label { Text = "Turno 1: 08:00–15:00 · Turno 2: 15:00–00:00. Cada factura emitida se asigna al turno vigente; fuera de esos horarios no se emiten ventas." },
                    new Label { Text = "Nota de seguridad: para autorizar cierres con diferencia se requiere un rol de administrador verificado; esa configuración todavía no existe. Los cierres con diferencia quedan bloqueados.", TextColor = Color.FromArgb("#9A5A00") },
                    _estado, abrir, cerrar, impresora, actualizar,
                    new Label { Text = "Historial de cierres", FontSize = 18, FontAttributes = FontAttributes.Bold }, _historial
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await CargarAsync();
    }

    private string UsuarioId => _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para administrar los turnos.");

    private async Task CargarAsync()
    {
        try
        {
            var actual = await _db.ObtenerTurnoAbiertoAsync(UsuarioId);
            _estado.Text = actual is null
                ? "No hay un turno abierto. El turno se puede abrir solo dentro del horario correspondiente."
                : $"Turno {actual.NumeroTurno} abierto · {actual.Fecha:dd/MM/yyyy} · desde {actual.HoraInicio:HH:mm}";
            _historial.Children.Clear();
            foreach (var turno in (await _db.ObtenerTurnosAsync(UsuarioId)).Take(20))
            {
                var resumen = turno.Estado == "C"
                    ? $"Cerrado {turno.HoraCierre:dd/MM/yyyy HH:mm} · Facturas: {turno.CantidadFacturas} · Total: RD$ {turno.TotalGeneral}"
                    : $"Abierto desde {turno.HoraInicio:dd/MM/yyyy HH:mm}";
                _historial.Children.Add(new Label { Text = $"Turno {turno.NumeroTurno} · {turno.Fecha:dd/MM/yyyy} · {resumen}", TextColor = Color.FromArgb("#60788F") });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo cargar el estado de los turnos.");
            _estado.Text = "No se pudo cargar el estado de los turnos.";
        }
    }

    private async void OnSeleccionarImpresoraClicked(object? sender, EventArgs e)
    {
        try { await _impresion.SeleccionarImpresoraAsync(this); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo seleccionar la impresora para el cierre de caja.");
            await DisplayAlertAsync("Impresoras", ex.Message, "Aceptar");
        }
    }

    private async void OnAbrirClicked(object? sender, EventArgs e)
    {
        try
        {
            var turno = await _db.AbrirTurnoActualAsync(UsuarioId);
            _estado.Text = $"Turno {turno.NumeroTurno} abierto desde {turno.HoraInicio:HH:mm}.";
            await CargarAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir el turno actual.");
            await DisplayAlertAsync("Turnos", ex.Message, "Aceptar");
        }
    }

    private async void OnCerrarClicked(object? sender, EventArgs e)
    {
        try
        {
            var turno = await _db.ObtenerTurnoAbiertoAsync(UsuarioId);
            if (turno is null) { await DisplayAlertAsync("Turnos", "No hay un turno abierto para cerrar.", "Aceptar"); return; }
            turno = await _db.ObtenerResumenTurnoAsync(turno.Id, UsuarioId);
            var resumen = $"Facturas: {turno.CantidadFacturas}\nTotal del sistema: RD$ {turno.TotalGeneral}\nEfectivo: RD$ {turno.TotalEfectivo}\nTarjeta: RD$ {turno.TotalTarjeta}\nTransferencia: RD$ {turno.TotalTransferencia}\nCrédito: RD$ {turno.TotalCredito}";
            if (!await DisplayAlertAsync("Cerrar turno", $"Turno {turno.NumeroTurno} del {turno.Fecha:dd/MM/yyyy}\n\n{resumen}\n\nNo se podrán emitir más facturas en él.", "Continuar", "Cancelar")) return;
            var contadoTexto = await DisplayPromptAsync("Cuadre de turno", "Introduce el total contado al cierre:", keyboard: Keyboard.Numeric);
            if (contadoTexto is null) return;
            if (!decimal.TryParse(contadoTexto.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var totalContado) || totalContado < 0)
            { await DisplayAlertAsync("Cuadre de turno", "Introduce un importe válido, igual o mayor que cero.", "Aceptar"); return; }
            var totalSistema = decimal.Parse(turno.TotalGeneral, CultureInfo.InvariantCulture);
            var diferencia = totalSistema - totalContado;
            var motivo = string.Empty;
            if (diferencia != 0)
            {
                await DisplayAlertAsync("Diferencia detectada", $"{(diferencia > 0 ? "Faltan" : "Sobran")} RD$ {Math.Abs(diferencia):N2}. La autorización administrativa para cerrar con diferencias estará disponible próximamente. El turno permanece abierto.", "Aceptar");
                return;
            }
            else await DisplayAlertAsync("Cuadre correcto", "Todo cuadra correctamente.", "Aceptar");
            var observaciones = await DisplayPromptAsync("Cierre de turno", "Observaciones adicionales (opcional):", initialValue: string.Empty);
            if (observaciones is null) return;
            var cerrado = await _db.CerrarTurnoAsync(turno.Id, UsuarioId, observaciones, totalContado, motivo, totalSistema);
            await CargarAsync();
            await DisplayAlertAsync("Cierre completado", $"Facturas: {cerrado.CantidadFacturas}\nSubtotal: RD$ {cerrado.TotalSubtotal}\nITBIS: RD$ {cerrado.TotalItbis}\nTotal del sistema: RD$ {cerrado.TotalSistema}\nTotal contado: RD$ {cerrado.TotalContado}\nDiferencia: RD$ {cerrado.Diferencia}\nJustificación: {cerrado.MotivoDiferencia}\n\nEfectivo: RD$ {cerrado.TotalEfectivo}\nTarjeta: RD$ {cerrado.TotalTarjeta}\nTransferencia: RD$ {cerrado.TotalTransferencia}\nCrédito: RD$ {cerrado.TotalCredito}", "Abrir reporte");
            try
            {
                var ruta = await CrearReporteAsync(cerrado);
                await _impresion.PrepararImpresoraSeleccionadaAsync();
                if (!await Launcher.Default.OpenAsync(new OpenFileRequest("Reporte de cierre de turno", new ReadOnlyFile(ruta))))
                    throw new InvalidOperationException("Windows no pudo abrir el reporte de cierre.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "El turno {TurnoId} se cerró, pero no se pudo abrir su reporte.", cerrado.Id);
                await DisplayAlertAsync("Reporte de cierre", $"El turno se cerró correctamente, pero no se pudo abrir el reporte: {ex.Message}", "Aceptar");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo cerrar el turno o generar su reporte.");
            await DisplayAlertAsync("Cierre de turno", ex.Message, "Aceptar");
        }
    }

    private static async Task<string> CrearReporteAsync(TurnoCaja turno)
    {
        static string E(string valor) => WebUtility.HtmlEncode(valor);
        var ruta = Path.Combine(FileSystem.CacheDirectory, $"cierre-turno-{turno.Fecha:yyyyMMdd}-{turno.NumeroTurno}.html");
        var html = $"<!doctype html><html lang='es'><meta charset='utf-8'><title>Cierre de caja</title><style>body{{font:16px Arial;max-width:760px;margin:40px auto;color:#222}}h1{{color:#354e64}}table{{border-collapse:collapse;width:100%}}td,th{{border:1px solid #bbb;padding:9px;text-align:left}}@media print{{button{{display:none}}}}</style><h1>Reporte de cierre de turno</h1><p>Turno {turno.NumeroTurno} · {turno.Fecha:dd/MM/yyyy}</p><p>Apertura: {turno.HoraInicio:dd/MM/yyyy HH:mm} · Cierre: {turno.HoraCierre:dd/MM/yyyy HH:mm}</p><table><tr><th>Concepto</th><th>Monto</th></tr><tr><td>Facturas emitidas</td><td>{turno.CantidadFacturas}</td></tr><tr><td>Subtotal</td><td>RD$ {turno.TotalSubtotal}</td></tr><tr><td>ITBIS</td><td>RD$ {turno.TotalItbis}</td></tr><tr><td>Total</td><td>RD$ {turno.TotalGeneral}</td></tr><tr><td>Efectivo</td><td>RD$ {turno.TotalEfectivo}</td></tr><tr><td>Tarjeta</td><td>RD$ {turno.TotalTarjeta}</td></tr><tr><td>Transferencia</td><td>RD$ {turno.TotalTransferencia}</td></tr><tr><td>Crédito</td><td>RD$ {turno.TotalCredito}</td></tr><tr><td>Total contado</td><td>RD$ {turno.TotalContado}</td></tr><tr><td>Diferencia (sistema - contado)</td><td>RD$ {turno.Diferencia}</td></tr></table><p>Justificación: {E(turno.MotivoDiferencia)}</p><p>Usuario: {E(turno.UsuarioCierra)}</p><p>Observaciones: {E(turno.Observaciones)}</p><button onclick='window.print()'>Imprimir reporte</button></html>";
        await File.WriteAllTextAsync(ruta, html, new UTF8Encoding(false));
        return ruta;
    }
}
