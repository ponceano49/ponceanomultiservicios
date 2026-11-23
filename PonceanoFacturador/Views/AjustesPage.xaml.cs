using System.Net.Mail;
using Microsoft.Extensions.Logging;
using PonceanoFacturador.Models;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public partial class AjustesPage : ContentPage
{
    private readonly DatabaseService _db;
    private readonly AuthService _auth;
    private readonly LicenciaService _licencia;
    private readonly ILogger<AjustesPage> _logger;
    private readonly List<(NcfUsuario Config, CheckBox Activo, Entry Prefijo, Entry Siguiente, Entry Final)> _controlesNcf = new();
    private string _logoRuta = string.Empty;
    private bool _ncfCargado;

    public AjustesPage(DatabaseService db, AuthService auth, LicenciaService licencia, ILogger<AjustesPage> logger)
    {
        InitializeComponent(); _db = db; _auth = auth; _licencia = licencia; _logger = logger;
        PagoPicker.ItemsSource = new[] { "Efectivo", "Transferencia", "Tarjeta", "Otro" };
        VencePicker.ItemsSource = new[] { "Ninguno", "3", "7", "14", "21", "30", "45", "60", "90", "180", "365" };
        MonedaPicker.ItemsSource = new[] { "Peso Dominicano", "Dólar", "Euro" };
        ColorPicker.ItemsSource = new[] { "Azul", "Verde", "Morado", "Gris" };
        CopiaFormatoPicker.ItemsSource = new[] { "PDF", "Imagen" };
        CopiaCanalPicker.ItemsSource = new[] { "Correo", "WhatsApp" };
        FormatoFechaPicker.ItemsSource = new[] { "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd" };
        IdiomaPicker.ItemsSource = new[] { "Español (República Dominicana)", "English (United States)" };
        TipoCuentaPicker.ItemsSource = new[] { "Ahorros", "Corriente" };
        UsuarioEntry.Text = $"{_auth.CurrentUser?.Nombre} — {_auth.CurrentUser?.Correo}";
        ServidorLicenciaEntry.Text = ServidorLicenciaConfig.Obtener();
        ImpuestoStepper.Value = 18;
        ImpuestoLabel.Text = "18%";
    }

    protected override async void OnAppearing() { base.OnAppearing(); await CargarAsync(); }

    private async Task CargarAsync()
    {
        try
        {
            var config = await _db.ObtenerAjustesAsync(_auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para ver los ajustes."));
            CompaniaEntry.Text = config.NombreCompania; RncEntry.Text = config.RncCompania; CorreoEntry.Text = config.CorreoFactura;
            TelefonosEntry.Text = config.Telefonos; Direccion1Entry.Text = config.Direccion1; Direccion2Entry.Text = config.Direccion2;
            _logoRuta = config.LogoRuta; LogoPreview.Source = _logoRuta; LogoPreview.IsVisible = !string.IsNullOrWhiteSpace(_logoRuta);
            PagoEditor.Text = config.InstruccionPago; PagoPicker.SelectedItem = config.OpcionPago;
            VencePicker.SelectedItem = config.VenceEnDias == 0 ? "Ninguno" : config.VenceEnDias.ToString();
            MonedaPicker.SelectedItem = config.Moneda; SimboloEntry.Text = config.SimboloMoneda;
            ImpuestoStepper.Value = (double)config.ImpuestoPorcentaje; ImpuestoIncluidoCheck.IsChecked = config.ImpuestoIncluido;
            NotaEditor.Text = config.NotaPorDefecto; CorreoMensajeEditor.Text = config.MensajeCorreo;
            CampoClienteEntry.Text = config.NombreCampoCliente; CampoRncEntry.Text = config.NombreCampoRnc;
            ColorPicker.SelectedItem = config.ColorTema; CopiaFormatoPicker.SelectedItem = config.EnviarCopiaComo; CopiaCanalPicker.SelectedItem = config.EnviarCopiaPor;
            EfectivoCheck.IsChecked = config.PagoEfectivo; TransferenciaCheck.IsChecked = config.PagoTransferencia;
            ChequeCheck.IsChecked = config.PagoCheque; TarjetaCheck.IsChecked = config.PagoTarjeta;
            CreditoCheck.IsChecked = config.PagoCredito; OtroCheck.IsChecked = config.PagoOtro; OtroMetodoEntry.Text = config.MetodoOtro;
            BancoEntry.Text = config.BancoNombre; NumeroCuentaEntry.Text = config.BancoNumeroCuenta;
            TipoCuentaPicker.SelectedItem = config.BancoTipoCuenta; BancoRncEntry.Text = config.BancoRnc;
            ReferenciaEntry.Text = config.MensajeReferencia; RecargoCheck.IsChecked = config.RecargoActivo;
            RecargoStepper.Value = (double)config.RecargoPorcentaje; RecargoLabel.Text = $"{config.RecargoPorcentaje:0.##}%";
            MotivoRecargoEntry.Text = config.RecargoMotivo; PayPalCheck.IsChecked = config.PayPalActivo; PayPalCorreoEntry.Text = config.PayPalCorreo;
            FormatoFechaPicker.SelectedItem = config.FormatoFecha;
            IdiomaPicker.SelectedItem = IdiomaPicker.Items.Contains(config.Idioma) ? config.Idioma : "Español (República Dominicana)";
            await CargarNcfAsync(_auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para ver los comprobantes."));
            var licencia = await _licencia.ObtenerEstadoLicenciaAsync();
            SuscripcionLabel.Text = $"Suscripción: {licencia.LicenciaTipo}; vencimiento: {licencia.LicenciaVencimiento?.ToLocalTime().ToString("dd/MM/yyyy") ?? "No aplica"}";
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudieron cargar los ajustes."); MensajeLabel.Text = "No se pudieron cargar los ajustes."; }
    }

    private async Task CargarNcfAsync(string usuarioId)
    {
        _ncfCargado = false;
        var configuraciones = await _db.ObtenerConfiguracionNcfAsync(usuarioId);
        NcfRows.Children.Clear();
        _controlesNcf.Clear();
        foreach (var config in configuraciones)
        {
            var activo = new CheckBox { IsChecked = config.Activo, HorizontalOptions = LayoutOptions.Center };
            var prefijo = new Entry { Text = config.Prefijo, Placeholder = "B01-", FontSize = 12 };
            var siguiente = new Entry { Text = config.SiguienteNumero.ToString(System.Globalization.CultureInfo.InvariantCulture), Keyboard = Keyboard.Numeric, FontSize = 12 };
            var final = new Entry { Text = config.NumeroFinal.ToString(System.Globalization.CultureInfo.InvariantCulture), Keyboard = Keyboard.Numeric, FontSize = 12 };
            var fila = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(new GridLength(1.3, GridUnitType.Star)), new(new GridLength(45)), new(new GridLength(80)), new(new GridLength(100)), new(new GridLength(100)) }, ColumnSpacing = 4, Padding = new Thickness(0, 4) };
            fila.Add(new Label { Text = $"{config.Tipo} — {config.Descripcion}", FontSize = 12, VerticalOptions = LayoutOptions.Center }, 0);
            fila.Add(activo, 1); fila.Add(prefijo, 2); fila.Add(siguiente, 3); fila.Add(final, 4);
            NcfRows.Children.Add(fila);
            _controlesNcf.Add((config, activo, prefijo, siguiente, final));
        }
        _ncfCargado = true;
    }

    private void OnImpuestoChanged(object? sender, ValueChangedEventArgs e) => ImpuestoLabel.Text = $"{e.NewValue:0.##}%";
    private void OnRecargoChanged(object? sender, ValueChangedEventArgs e) => RecargoLabel.Text = $"{e.NewValue:0.##}%";

    private void OnGuardarServidorLicenciaClicked(object? sender, EventArgs e)
    {
        try
        {
            ServidorLicenciaConfig.Guardar(ServidorLicenciaEntry.Text);
            ServidorLicenciaEntry.Text = ServidorLicenciaConfig.Obtener();
            ServidorLicenciaMensajeLabel.TextColor = Colors.DarkGreen;
            ServidorLicenciaMensajeLabel.Text = "Dirección guardada; la validación remota continúa usando Railway.";
        }
        catch (InvalidOperationException ex)
        {
            ServidorLicenciaMensajeLabel.TextColor = Colors.DarkRed;
            ServidorLicenciaMensajeLabel.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la dirección del servidor de licencias.");
            ServidorLicenciaMensajeLabel.TextColor = Colors.DarkRed;
            ServidorLicenciaMensajeLabel.Text = "No se pudo guardar la dirección del servidor.";
        }
    }

    private async void OnLogoClicked(object? sender, EventArgs e)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Selecciona el logo de la compañía", FileTypes = FilePickerFileType.Images });
            if (file == null) return;
            var destination = Path.Combine(FileSystem.AppDataDirectory, $"logo-{_auth.CurrentUser!.IdFirebase}.img");
            await using var source = await file.OpenReadAsync();
            await using var target = File.Create(destination);
            await source.CopyToAsync(target);
            _logoRuta = destination; LogoPreview.Source = destination; LogoPreview.IsVisible = true;
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo cargar el logo."); MensajeLabel.Text = "No se pudo cargar el logo."; }
    }

    private async void OnGuardarClicked(object? sender, EventArgs e)
    {
        try
        {
            var usuarioId = _auth.CurrentUser?.IdFirebase ?? throw new InvalidOperationException("Inicia sesión para guardar los ajustes.");
            if (!_ncfCargado) throw new InvalidOperationException("No se cargaron las secuencias NCF. Vuelve a abrir Ajustes e inténtalo de nuevo.");
            var nombreCompania = CompaniaEntry.Text?.Trim() ?? string.Empty;
            var correoFactura = CorreoEntry.Text?.Trim() ?? string.Empty;
            if (nombreCompania.Length == 0) throw new InvalidOperationException("El nombre de la empresa es obligatorio.");
            if (correoFactura.Length > 0 && !MailAddress.TryCreate(correoFactura, out _)) throw new InvalidOperationException("Introduce un correo válido para la factura.");
            var existente = await _db.ObtenerAjustesAsync(usuarioId);
            var configuracionesNcf = new List<NcfUsuario>();
            foreach (var (secuencia, activo, prefijo, siguiente, final) in _controlesNcf)
            {
                if (!int.TryParse(siguiente.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var siguienteNumero) ||
                    !int.TryParse(final.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numeroFinal))
                    throw new InvalidOperationException($"Los números de la secuencia {secuencia.Tipo} deben ser enteros.");
                var prefijoNcf = prefijo.Text?.Trim() ?? string.Empty;
                if ((prefijoNcf != secuencia.Tipo && prefijoNcf != secuencia.Tipo + "-") || siguienteNumero is < 1 or > 100_000_000 || numeroFinal is < 0 or > 99_999_999 || (activo.IsChecked && numeroFinal == 0))
                    throw new InvalidOperationException($"Revisa el prefijo, la secuencia y el rango autorizado para {secuencia.Tipo}.");
                if (siguienteNumero < secuencia.SiguienteNumero)
                    throw new InvalidOperationException($"No se puede retroceder la secuencia {secuencia.Tipo}; ya se reservaron números hasta {secuencia.SiguienteNumero - 1:D8}.");
                if (secuencia.SiguienteNumero > 1 && prefijoNcf != secuencia.Prefijo)
                    throw new InvalidOperationException($"No se puede cambiar el prefijo de {secuencia.Tipo} después de emitir comprobantes.");
                configuracionesNcf.Add(new NcfUsuario
                {
                    Id = secuencia.Id, UsuarioId = usuarioId, Tipo = secuencia.Tipo, Descripcion = secuencia.Descripcion,
                    Activo = activo.IsChecked, Prefijo = prefijoNcf,
                    SiguienteNumero = siguienteNumero, NumeroFinal = numeroFinal
                });
            }
            var config = new AjustesUsuario
            {
                UsuarioId = usuarioId, NombreCompania = nombreCompania, RncCompania = RncEntry.Text?.Trim() ?? string.Empty,
                CorreoFactura = correoFactura, Telefonos = TelefonosEntry.Text?.Trim() ?? string.Empty,
                Direccion1 = Direccion1Entry.Text?.Trim() ?? string.Empty, Direccion2 = Direccion2Entry.Text?.Trim() ?? string.Empty,
                LogoRuta = _logoRuta, InstruccionPago = PagoEditor.Text ?? string.Empty, OpcionPago = PagoPicker.SelectedItem?.ToString() ?? "Efectivo",
                VenceEnDias = int.TryParse(VencePicker.SelectedItem?.ToString(), out var dias) ? dias : 0,
                Moneda = MonedaPicker.SelectedItem?.ToString() ?? "Peso Dominicano", SimboloMoneda = SimboloEntry.Text?.Trim() ?? "RD$",
                ImpuestoPorcentaje = (decimal)ImpuestoStepper.Value, ImpuestoIncluido = ImpuestoIncluidoCheck.IsChecked,
                NotaPorDefecto = NotaEditor.Text ?? string.Empty, MensajeCorreo = CorreoMensajeEditor.Text ?? string.Empty,
                NombreCampoCliente = CampoClienteEntry.Text?.Trim() ?? "Cliente", NombreCampoRnc = CampoRncEntry.Text?.Trim() ?? "RNC/Cédula",
                ColorTema = ColorPicker.SelectedItem?.ToString() ?? "Azul", EnviarCopiaComo = CopiaFormatoPicker.SelectedItem?.ToString() ?? "PDF",
                EnviarCopiaPor = CopiaCanalPicker.SelectedItem?.ToString() ?? "Correo",
                NcfB01 = existente.NcfB01, NcfB02 = existente.NcfB02, NcfB03 = existente.NcfB03, NcfB04 = existente.NcfB04, NcfB15 = existente.NcfB15,
                FormatoFecha = FormatoFechaPicker.SelectedItem?.ToString() ?? "dd/MM/yyyy", Idioma = IdiomaPicker.SelectedItem?.ToString() ?? "Español (República Dominicana)",
                PagoEfectivo = EfectivoCheck.IsChecked, PagoTransferencia = TransferenciaCheck.IsChecked,
                PagoCheque = ChequeCheck.IsChecked, PagoTarjeta = TarjetaCheck.IsChecked, PagoCredito = CreditoCheck.IsChecked,
                PagoOtro = OtroCheck.IsChecked, MetodoOtro = OtroMetodoEntry.Text?.Trim() ?? string.Empty,
                BancoNombre = BancoEntry.Text?.Trim() ?? string.Empty, BancoNumeroCuenta = NumeroCuentaEntry.Text?.Trim() ?? string.Empty,
                BancoTipoCuenta = TipoCuentaPicker.SelectedItem?.ToString() ?? "Ahorros", BancoRnc = BancoRncEntry.Text?.Trim() ?? string.Empty,
                MensajeReferencia = ReferenciaEntry.Text?.Trim() ?? string.Empty, RecargoActivo = RecargoCheck.IsChecked,
                RecargoPorcentaje = (decimal)RecargoStepper.Value, RecargoMotivo = MotivoRecargoEntry.Text?.Trim() ?? string.Empty,
                StripeActivo = existente.StripeActivo, PayPalActivo = PayPalCheck.IsChecked, PayPalCorreo = PayPalCorreoEntry.Text?.Trim() ?? string.Empty
            };
            if (PayPalCheck.IsChecked && !MailAddress.TryCreate(PayPalCorreoEntry.Text?.Trim(), out _))
                throw new InvalidOperationException("Introduce un correo válido para PayPal o desactiva esa opción. Esta app aún no procesa cobros PayPal.");
            await _db.GuardarAjustesAsync(config);
            await _db.GuardarConfiguracionNcfAsync(usuarioId, configuracionesNcf);
            MensajeLabel.TextColor = Colors.DarkGreen; MensajeLabel.Text = "Ajustes guardados correctamente.";
        }
        catch (InvalidOperationException ex)
        {
            MensajeLabel.TextColor = Colors.DarkRed;
            MensajeLabel.Text = ex.Message;
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudieron guardar los ajustes."); MensajeLabel.TextColor = Colors.DarkRed; MensajeLabel.Text = "No se pudieron guardar los ajustes. Inténtalo de nuevo."; }
    }

    private async void OnStripeClicked(object? sender, EventArgs e)
    {
        try { await DisplayAlertAsync("Configuración de Stripe", "La integración de pagos no está implementada. No introduzcas ni compartas claves secretas aquí. Stripe no se activará y no se mostrarán botones de cobro hasta integrar un proveedor de pagos seguro.", "Cerrar"); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo mostrar la información de Stripe."); MensajeLabel.Text = "No se pudo mostrar la información de Stripe."; }
    }

    private async void OnExportarClicked(object? sender, EventArgs e)
    {
        try
        {
            var clientes = await _db.GetAllClientesAsync();
            var csv = "Nombre,RNC/Cedula,Telefono,Correo\n" + string.Join("\n", clientes.Select(c => string.Join(',', Csv(c.Nombre), Csv(c.RncCedula), Csv(c.Telefono), Csv(c.Correo))));
            var path = Path.Combine(FileSystem.CacheDirectory, "clientes.csv");
            await File.WriteAllTextAsync(path, csv);
            await Share.Default.RequestAsync(new ShareFileRequest("Exportación de clientes", new ShareFile(path)));
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo exportar CSV."); MensajeLabel.Text = "No se pudo exportar el archivo CSV."; }
    }

    private static string Csv(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
    private async void OnCancelarClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
