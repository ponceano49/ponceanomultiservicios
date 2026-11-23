using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class NotasCreditoPage : ContentPage, IShellToolbarActionPage
{
    public Button ShellToolbarActionButton => NuevoContentButton;

    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly IServiceProvider _services;
    private readonly ILogger<NotasCreditoPage> _logger;
    private List<NotaCredito> _notas = new();
    private bool? _mobileHeader;

    public NotasCreditoPage(DatabaseService db, AuthService auth, IServiceProvider services, ILogger<NotasCreditoPage> logger)
    {
        InitializeComponent(); _db = db; _auth = auth; _services = services; _logger = logger;
#if ANDROID
        NotasIconoLabel.IsVisible = NotasTituloLabel.IsVisible = NuevoContentButton.IsVisible = false;
        AndroidMobileUi.EnableHorizontalTableScroll(TablaContainer, 720);
#endif
        ToolbarItems.Remove(ToolbarItems.First(item => item.Text == "+ Nuevo"));
        FiltroPicker.ItemsSource = new[] { "Todo", "Borrador", "Emitida", "Anulada" };
        FiltroPicker.SelectedIndex = 0;
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
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(HeaderActions, 0);
            Grid.SetColumn(HeaderActions, 0);
            Grid.SetColumn(SearchFilters, 0);
            Grid.SetRow(SearchFilters, 1);
            SearchFilters.HorizontalOptions = LayoutOptions.Fill;
        }
        else
        {
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(HeaderActions, 0);
            Grid.SetColumn(HeaderActions, 0);
            Grid.SetRow(SearchFilters, 0);
            Grid.SetColumn(SearchFilters, 1);
            SearchFilters.HorizontalOptions = LayoutOptions.End;
        }
    }

    protected override async void OnAppearing() { base.OnAppearing(); await CargarAsync(); }

    private async Task CargarAsync()
    {
        try
        {
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para ver las notas de crédito.");
            _notas = await _db.GetAllNotasCreditoAsync(uid);
            AplicarFiltro();
            MensajeLabel.Text = string.Empty;
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudieron cargar las notas de crédito."); MensajeLabel.Text = "No se pudieron cargar las notas de crédito."; }
    }

    private async void OnNuevaClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PushAsync(ActivatorUtilities.CreateInstance<NotaCreditoEditarPage>(_services)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo abrir la nueva nota de crédito."); MensajeLabel.Text = "No se pudo abrir el formulario."; }
    }

    private void OnFiltroChanged(object? sender, EventArgs e) => AplicarFiltro();
    private void OnBuscarChanged(object? sender, TextChangedEventArgs e) => AplicarFiltro();
    private void OnBusquedaProfundaChanged(object? sender, CheckedChangedEventArgs e) => AplicarFiltro();

    private void AplicarFiltro()
    {
        var filtro = FiltroPicker.SelectedItem?.ToString() ?? "Todo";
        var texto = BuscarBar.Text?.Trim() ?? string.Empty;
        Lista.ItemsSource = _notas.Where(n => (filtro == "Todo" || n.Estado == filtro) &&
            (texto.Length == 0 || n.NCF.Contains(texto, StringComparison.CurrentCultureIgnoreCase) ||
             n.ClienteNombre.Contains(texto, StringComparison.CurrentCultureIgnoreCase) ||
             (BusquedaProfundaCheck.IsChecked && (n.ClienteRnc.Contains(texto, StringComparison.CurrentCultureIgnoreCase) ||
              n.Motivo.Contains(texto, StringComparison.CurrentCultureIgnoreCase) || n.Items.Any(i => i.Descripcion.Contains(texto, StringComparison.CurrentCultureIgnoreCase))))))
            .OrderByDescending(n => n.Fecha).ToList();
    }

    private async void OnFilaTapped(object? sender, TappedEventArgs e)
    {
        if (sender is View { BindingContext: NotaCredito nota }) await MostrarDetalleAsync(nota);
    }

    private async void OnFilaAccionesClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: NotaCredito nota }) return;
        var boton = (Button)sender;
        var opciones = nota.EsBorrador ? new[] { "Editar" } : nota.Anulada ? new[] { "Ver detalle", "Crear copia" } : new[] { "Ver detalle", "Crear copia", "Anular" };
#if ANDROID
        var opcion = await AndroidMobileUi.ShowRowActionsAsync(boton, $"Nota {nota.NCF}", opciones);
#else
        var opcion = await DisplayActionSheetAsync($"Nota {nota.NCF}", "Cancelar", null, opciones);
#endif
        if (opcion is "Ver detalle" or "Editar") await MostrarDetalleAsync(nota);
        else if (opcion == "Crear copia") await CrearCopiaAsync(nota);
        else if (opcion == "Anular") await AnularAsync(nota);
    }

    private async Task MostrarDetalleAsync(NotaCredito nota)
    {
        try { await Navigation.PushAsync(new NotaCreditoEditarPage(_db, _auth, _services.GetRequiredService<ILogger<NotaCreditoEditarPage>>(), nota: nota)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo mostrar el detalle de la nota."); MensajeLabel.Text = "No se pudo mostrar el detalle."; }
    }

    private async Task CrearCopiaAsync(NotaCredito nota)
    {
        try { await Navigation.PushAsync(new NotaCreditoEditarPage(_db, _auth, _services.GetRequiredService<ILogger<NotaCreditoEditarPage>>(), nota: nota, esCopia: true)); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo copiar la nota de crédito."); MensajeLabel.Text = "No se pudo crear la copia."; }
    }

    private async Task AnularAsync(NotaCredito nota)
    {
        if (nota.Anulada) return;
        try
        {
            if (!await DisplayAlertAsync("Anular nota de crédito", "¿Está seguro de anular esta nota de crédito?", "Anular", "Cancelar")) return;
            var uid = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para continuar.");
            await _db.AnularNotaCreditoAsync(nota.Id, uid);
            await DisplayAlertAsync("Notas de crédito", "La nota de crédito fue anulada.", "Aceptar");
            await CargarAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo anular la nota."); MensajeLabel.Text = ex.Message; }
    }

    private async void OnVolverClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//modulo-0"); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo volver al resumen."); MensajeLabel.Text = "No se pudo volver al resumen."; }
    }
}
