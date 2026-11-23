using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class ClientesPage : ContentPage, IShellToolbarActionPage
{
    public Button ShellToolbarActionButton => NuevoButton;

    private readonly DatabaseService _db;
    private readonly RncContribuyentesService _rncLocal;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ClientesPage> _logger;
    private int _searchVersion;
    private bool _busy;
    private bool? _mobileHeader;

    public ClientesPage(DatabaseService db, RncContribuyentesService rncLocal, ILoggerFactory loggerFactory, ILogger<ClientesPage> logger)
    {
        InitializeComponent();
        _db = db;
        _rncLocal = rncLocal;
        _loggerFactory = loggerFactory;
        _logger = logger;
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
            Grid.SetColumn(InformacionButton, 2);
            Grid.SetRow(BuscarBar, 1);
            Grid.SetColumn(BuscarBar, 0);
            Grid.SetColumnSpan(BuscarBar, 3);
            BuscarBar.HeightRequest = 48;
            InformacionButton.WidthRequest = 44;
            InformacionButton.HeightRequest = 44;
            InformacionButton.MinimumHeightRequest = 44;
        }
        else
        {
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Star, new GridLength(360) })
                HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(width));
            HeaderGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(InformacionButton, 2);
            Grid.SetColumn(BuscarBar, 4);
            Grid.SetRow(BuscarBar, 0);
            Grid.SetColumnSpan(BuscarBar, 1);
            BuscarBar.HeightRequest = 38;
            InformacionButton.WidthRequest = 34;
            InformacionButton.HeightRequest = 32;
            InformacionButton.MinimumHeightRequest = 32;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await CargarAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _searchVersion++;
    }

    private async Task CargarAsync()
    {
        var version = ++_searchVersion;
        try
        {
            var texto = BuscarBar.Text?.Trim() ?? string.Empty;
            var clientes = texto.Length == 0 ? await _db.GetAllClientesAsync() : await _db.BuscarClientesAsync(texto);
            if (version != _searchVersion) return;
            ClientesLista.ItemsSource = clientes;
            ClientesLista.SelectedItem = null;
            MensajeLabel.Text = string.Empty;
        }
        catch (Exception ex)
        {
            if (version != _searchVersion) return;
            _logger.LogError(ex, "No se pudo cargar la lista de clientes.");
            MensajeLabel.Text = ex is InvalidOperationException ? ex.Message : "No se pudieron cargar los clientes. Inténtalo de nuevo.";
        }
    }

    private async void OnBuscarChanged(object? sender, TextChangedEventArgs e) => await CargarAsync();

    private async void OnInformacionClicked(object? sender, EventArgs e)
    {
        await DisplayAlertAsync("Clientes", "Administra aquí los clientes que puedes seleccionar al crear facturas, cotizaciones y notas de crédito.", "Aceptar");
    }

    private async void OnNuevoClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PushAsync(new ClienteEditarPage(_db, _rncLocal, _loggerFactory.CreateLogger<ClienteEditarPage>())); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir el formulario de clientes.");
            await DisplayAlertAsync("Clientes", "No se pudo abrir el formulario.", "Aceptar");
        }
    }

    private async void OnFilaEditarClicked(object? sender, EventArgs e)
    {
        if (_busy || sender is not Button { BindingContext: Cliente cliente }) return;
        try { await Navigation.PushAsync(new ClienteEditarPage(_db, _rncLocal, _loggerFactory.CreateLogger<ClienteEditarPage>(), cliente)); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir el cliente para editar.");
            await DisplayAlertAsync("Clientes", "No se pudo abrir el cliente.", "Aceptar");
        }
    }

    private async void OnFilaEliminarClicked(object? sender, EventArgs e)
    {
        if (_busy || sender is not Button { BindingContext: Cliente cliente }) return;
        _busy = true;
        try
        {
            if (!await DisplayAlertAsync("Eliminar cliente", $"¿Deseas eliminar a {cliente.Nombre}? Esta acción no se puede deshacer.", "Eliminar", "Cancelar")) return;
            if (await _db.EliminarClienteAsync(cliente.Id) == 0)
                await DisplayAlertAsync("Clientes", "El cliente ya no existe.", "Aceptar");
            else
                await DisplayAlertAsync("Clientes", "Cliente eliminado correctamente.", "Aceptar");
            await CargarAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo eliminar el cliente.");
            await DisplayAlertAsync("Clientes", "No se pudo eliminar el cliente. Inténtalo de nuevo.", "Aceptar");
        }
        finally { _busy = false; }
    }

    private async void OnVolverClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("//modulo-0");
}
