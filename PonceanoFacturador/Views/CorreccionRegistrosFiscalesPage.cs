using System.Globalization;
using PonceanoFacturador.Services;

namespace PonceanoFacturador.Views;

public sealed class CorreccionRegistrosFiscalesPage : ContentPage
{
    private static readonly string[] Tipos607 =
    {
        "01 - Ingresos por operaciones (no financieros)", "02 - Ingresos financieros",
        "03 - Ingresos extraordinarios", "04 - Ingresos por arrendamientos",
        "05 - Ingresos por venta de activos depreciables", "06 - Otros ingresos"
    };

    private static readonly string[] Tipos606 =
    {
        "01 - Gastos de personal", "02 - Trabajos, suministros y servicios", "03 - Arrendamientos",
        "04 - Gastos de activos fijos", "05 - Gastos de representación", "06 - Otras deducciones admitidas",
        "07 - Gastos financieros", "08 - Gastos extraordinarios", "09 - Compras para costo de venta",
        "10 - Adquisiciones de activos", "11 - Gastos de seguros"
    };

    private readonly DatabaseService _db;
    private readonly FormatoEnvioDgiiService _formatoDgii;
    private readonly string _usuarioId;
    private readonly string _tipo;
    private readonly DateTime _periodo;
    private readonly string _rncContribuyente;
    private readonly Func<ResultadoExportacionFiscal, Task> _archivoGenerado;
    private readonly Label _errores = new() { TextColor = Color.FromArgb("#C62828") };
    private readonly Label _estado = new() { TextColor = Color.FromArgb("#60788F") };
    private readonly Picker _registro = new() { Title = "Selecciona un registro con errores" };
    private readonly Entry _ncf = new() { IsReadOnly = true };
    private readonly Entry _rnc = new() { Placeholder = "RNC / cédula", Keyboard = Keyboard.Numeric };
    private readonly Entry _razonSocial = new() { Placeholder = "RNC / Razón Social" };
    private readonly Picker _tipoFiscal = new() { Title = "Código fiscal" };
    private readonly Entry _montoGravado = new() { Placeholder = "Monto gravado", Keyboard = Keyboard.Numeric };
    private readonly Entry _itbis = new() { Placeholder = "ITBIS", Keyboard = Keyboard.Numeric };
    private readonly Entry _total = new() { Placeholder = "Total", Keyboard = Keyboard.Numeric };
    private readonly Entry _montoServicios = new() { Placeholder = "Monto facturado en servicios (606)", Keyboard = Keyboard.Numeric };
    private readonly Entry _montoBienes = new() { Placeholder = "Monto facturado en bienes (606)", Keyboard = Keyboard.Numeric };
    private readonly Entry _montoExento = new() { Placeholder = "Monto exento", Keyboard = Keyboard.Numeric };
    private readonly Button _guardar = new() { Text = "✅ GUARDAR CORRECCIÓN" };
    private readonly Button _calcularTotal = new() { Text = "Calcular total" };
    private readonly CampoValidable _campoNcf;
    private readonly CampoValidable _campoRnc;
    private readonly CampoValidable _campoRazonSocial;
    private readonly CampoValidable _campoTipoFiscal;
    private readonly CampoValidable _campoMontoGravado;
    private readonly CampoValidable _campoItbis;
    private readonly CampoValidable _campoTotal;
    private readonly CampoValidable _campoMontoServicios;
    private readonly CampoValidable _campoMontoBienes;
    private readonly CampoValidable _campoMontoExento;

    private sealed record CampoValidable(VerticalStackLayout Vista, Border Borde, BoxView Subrayado, Label Aviso);
    private sealed record OpcionFiscal(string Codigo, string Descripcion);
    private readonly Button _revalidar = new() { Text = "🔄 VOLVER A VALIDAR" };
    private readonly List<ProblemaRegistroFiscal> _problemas = new();
    private readonly HashSet<string> _registrosEnviados = new(StringComparer.Ordinal);
    private bool _noSePudoVerificarEnvios;
    private bool _cargandoRegistro;
    private bool _actualizandoTotal;

    public CorreccionRegistrosFiscalesPage(DatabaseService db, FormatoEnvioDgiiService formatoDgii,
        string usuarioId, string tipo, DateTime periodo, string rncContribuyente,
        Func<ResultadoExportacionFiscal, Task> archivoGenerado)
    {
        _db = db;
        _formatoDgii = formatoDgii;
        _usuarioId = usuarioId;
        _tipo = tipo;
        _periodo = periodo;
        _rncContribuyente = rncContribuyente;
        _archivoGenerado = archivoGenerado;
        Title = "Corregir registros fiscales";

        _campoNcf = CrearCampoValidable(_ncf);
        _campoRnc = CrearCampoValidable(_rnc);
        _campoRazonSocial = CrearCampoValidable(_razonSocial);
        _campoTipoFiscal = CrearCampoValidable(_tipoFiscal);
        _campoMontoGravado = CrearCampoValidable(_montoGravado);
        _campoItbis = CrearCampoValidable(_itbis);
        _campoTotal = CrearCampoValidable(_total);
        _campoMontoServicios = CrearCampoValidable(_montoServicios);
        _campoMontoBienes = CrearCampoValidable(_montoBienes);
        _campoMontoExento = CrearCampoValidable(_montoExento);
        _campoMontoServicios.Vista.IsVisible = _campoMontoBienes.Vista.IsVisible = tipo == "606";
        _tipoFiscal.ItemDisplayBinding = new Binding(nameof(OpcionFiscal.Descripcion));
        _tipoFiscal.ItemsSource = (tipo == "607" ? Tipos607 : Tipos606)
            .Select(descripcion => new OpcionFiscal(descripcion[..2], descripcion)).ToArray();
        _registro.SelectedIndexChanged += OnRegistroSeleccionado;
        _tipoFiscal.SelectedIndexChanged += (_, _) => { if (!_cargandoRegistro) ValidarCamposEditables(false); };
        _razonSocial.TextChanged += (_, _) => ValidarCamposEditables(false);
        _montoGravado.TextChanged += (_, _) => ValidarCamposEditables(true);
        _itbis.TextChanged += (_, _) => ValidarCamposEditables(true);
        _montoExento.TextChanged += (_, _) => ValidarCamposEditables(true);
        _montoServicios.TextChanged += (_, _) => ValidarCamposEditables(false);
        _montoBienes.TextChanged += (_, _) => ValidarCamposEditables(false);
        _total.TextChanged += (_, _) => ValidarCamposEditables(false);
        _calcularTotal.Clicked += (_, _) => ValidarCamposEditables(true);
        _guardar.Clicked += GuardarCorreccion;
        _revalidar.Clicked += VolverAValidar;
        var cancelar = new Button { Text = "❌ CANCELAR" };
        cancelar.Clicked += async (_, _) => await Navigation.PopAsync();

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 12,
                MaximumWidthRequest = 850,
                Children =
                {
                    new Label { Text = $"📝 CORREGIR REGISTROS FISCALES ({tipo})", FontSize = 24, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "⚠️ ADVERTENCIA: Los cambios que realice aquí afectan directamente el registro fiscal que se exportará para la DGII. Verifique cuidadosamente antes de guardar.", TextColor = Color.FromArgb("#9A5A00") },
                    new Label { Text = "🔴 CAMPOS MARCADOS EN ROJO SON LOS QUE REQUIEREN CORRECCIÓN ANTES DE GENERAR EL ARCHIVO TXT PARA LA DGII.", TextColor = Color.FromArgb("#C62828") },
                    new Label { Text = "Registros con errores de validación:", FontAttributes = FontAttributes.Bold },
                    _errores,
                    _registro,
                    new Label { Text = "NCF / Comprobante Fiscal (solo lectura)" }, _campoNcf.Vista,
                    new Label { Text = "RNC / Razón Social" }, _campoRnc.Vista, _campoRazonSocial.Vista,
                    new Label { Text = tipo == "607" ? "Código tipo de ingreso (01-06)" : "Código tipo de bienes y servicios (01-11)" }, _campoTipoFiscal.Vista,
                    new Label { Text = "Monto Gravado" }, _campoMontoGravado.Vista,
                    new Label { Text = "Monto facturado en Servicios (606)", IsVisible = tipo == "606" }, _campoMontoServicios.Vista,
                    new Label { Text = "Monto facturado en Bienes (606)", IsVisible = tipo == "606" }, _campoMontoBienes.Vista,
                    new Label { Text = "Monto Exento" }, _campoMontoExento.Vista,
                    new Label { Text = "ITBIS" }, _campoItbis.Vista,
                    new Label { Text = "Total" }, _campoTotal.Vista, _calcularTotal,
                    _guardar, _revalidar, cancelar, _estado
                }
            }
        };
    }

    private static CampoValidable CrearCampoValidable(View control)
    {
        var borde = new Border
        {
            Stroke = Color.FromArgb("#D5DEE5"),
            StrokeThickness = 1,
            Padding = new Thickness(2),
            Content = control
        };
        var subrayado = new BoxView { HeightRequest = 3, Color = Color.FromArgb("#C62828"), IsVisible = false };
        var aviso = new Label { TextColor = Color.FromArgb("#C62828"), FontSize = 12, IsVisible = false };
        return new CampoValidable(new VerticalStackLayout { Spacing = 2, Children = { borde, subrayado, aviso } }, borde, subrayado, aviso);
    }

    private static void AplicarError(CampoValidable campo, string? mensaje)
    {
        var tieneError = !string.IsNullOrWhiteSpace(mensaje);
        campo.Borde.Stroke = Color.FromArgb(tieneError ? "#C62828" : "#D5DEE5");
        campo.Borde.StrokeThickness = tieneError ? 2 : 1;
        campo.Subrayado.IsVisible = tieneError;
        campo.Aviso.Text = tieneError ? $"⚠️ {mensaje}" : string.Empty;
        campo.Aviso.IsVisible = tieneError;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await CargarProblemasAsync();
    }

    private async Task<bool> CargarProblemasAsync(string? registroPreferido = null)
    {
        try
        {
            var ncfsEnviados = await _db.ObtenerNcfIncluidosEnEnviosAsync(_usuarioId, _tipo, _periodo.Year, _periodo.Month);
            var problemasActuales = await _db.RevalidarRegistrosFiscalesAsync(_usuarioId, _tipo, _periodo.Year, _periodo.Month);
            _cargandoRegistro = true;
            _registro.ItemsSource = null;
            _problemas.Clear();
            _registrosEnviados.Clear();
            _noSePudoVerificarEnvios = false;
            _problemas.AddRange(problemasActuales);
            foreach (var problema in _problemas)
                if (ncfsEnviados.Contains(problema.Registro.Ncf)) _registrosEnviados.Add(problema.Registro.Id);

            _errores.Text = _problemas.Count == 0
                ? "✅ No hay registros con errores de validación."
                : string.Join(Environment.NewLine, _problemas.SelectMany(problema => problema.Errores));
            _registro.ItemsSource = _problemas.Select(problema =>
                $"{(_registrosEnviados.Contains(problema.Registro.Id) ? "🔒 ENVIADO · " : string.Empty)}{(string.IsNullOrWhiteSpace(problema.Registro.Ncf) ? problema.Registro.Id : problema.Registro.Ncf)} — {problema.Errores[0]}").ToArray();
            var indicePreferido = _problemas.FindIndex(problema => problema.Registro.Id == registroPreferido);
            _registro.SelectedIndex = indicePreferido >= 0 ? indicePreferido : _problemas.Count > 0 ? 0 : -1;
            _cargandoRegistro = false;
            if (_registro.SelectedIndex >= 0) OnRegistroSeleccionado(_registro, EventArgs.Empty);
            else
            {
                _cargandoRegistro = true;
                try
                {
                    _ncf.Text = _rnc.Text = _razonSocial.Text = string.Empty;
                    _montoGravado.Text = _montoServicios.Text = _montoBienes.Text = _montoExento.Text = string.Empty;
                    _itbis.Text = _total.Text = string.Empty;
                    _tipoFiscal.SelectedIndex = -1;
                }
                finally { _cargandoRegistro = false; }
                ActualizarMarcas(Array.Empty<string>());
            }
            var indiceSeleccionado = _registro.SelectedIndex;
            _guardar.IsEnabled = !_noSePudoVerificarEnvios && indiceSeleccionado >= 0 &&
                !_registrosEnviados.Contains(_problemas[indiceSeleccionado].Registro.Id);
            _revalidar.IsEnabled = !_noSePudoVerificarEnvios;
            if (_noSePudoVerificarEnvios)
            {
                _estado.Text = "No se pudo verificar de forma segura una copia archivada; la edición queda bloqueada.";
                HabilitarCampos(false);
            }
            else if (_problemas.Count == 0)
            {
                _estado.Text = "No hay registros pendientes de corrección.";
                HabilitarCampos(false);
            }
            else
            {
                var enviado = indiceSeleccionado >= 0 && _registrosEnviados.Contains(_problemas[indiceSeleccionado].Registro.Id);
                _estado.Text = enviado ? $"El NCF {_problemas[indiceSeleccionado].Registro.Ncf} aparece en una copia de envío confirmada y no puede modificarse." : string.Empty;
                HabilitarCampos(!enviado);
            }
            return true;
        }
        catch (Exception ex)
        {
            _cargandoRegistro = false;
            _noSePudoVerificarEnvios = true;
            _estado.Text = $"No se pudieron cargar los errores de validación: {ex.Message}";
            _guardar.IsEnabled = false;
            _revalidar.IsEnabled = false;
            HabilitarCampos(false);
            ActualizarMarcas(Array.Empty<string>());
            return false;
        }
    }

    private void OnRegistroSeleccionado(object? sender, EventArgs e)
    {
        var indice = _registro.SelectedIndex;
        if (_cargandoRegistro || indice < 0 || indice >= _problemas.Count) return;
        var fiscal = _problemas[indice].Registro;
        _cargandoRegistro = true;
        try
        {
            _ncf.Text = fiscal.Ncf;
            _rnc.Text = fiscal.RncContraparte;
            _razonSocial.Text = fiscal.RazonSocialContraparte;
            _montoGravado.Text = fiscal.MontoGravadoTotal;
            _montoServicios.Text = fiscal.MontoGravadoServicios;
            _montoBienes.Text = fiscal.MontoGravadoBienes;
            _montoExento.Text = fiscal.MontoExento;
            _itbis.Text = fiscal.ItbisFacturado;
            _total.Text = fiscal.TotalFacturado;
            var codigo = (_tipo == "607" ? fiscal.TipoIngresoGasto : fiscal.TipoBienServicio)?.Trim() ?? string.Empty;
            _tipoFiscal.SelectedItem = ((IEnumerable<OpcionFiscal>)_tipoFiscal.ItemsSource)
                .FirstOrDefault(opcion => codigo.Equals(opcion.Codigo, StringComparison.Ordinal) ||
                    codigo.Equals(opcion.Descripcion, StringComparison.OrdinalIgnoreCase));
        }
        finally { _cargandoRegistro = false; }
        var enviado = _registrosEnviados.Contains(fiscal.Id);
        HabilitarCampos(!_noSePudoVerificarEnvios && !enviado);
        _guardar.IsEnabled = !_noSePudoVerificarEnvios && !enviado;
        ActualizarMarcas(_problemas[indice].Errores);
        ValidarCamposEditables(false);
        _estado.Text = enviado ? $"El NCF {fiscal.Ncf} aparece en una copia de envío confirmada y no puede modificarse." : string.Empty;
    }

    private void ActualizarMarcas(IReadOnlyList<string> errores)
    {
        var rncInvalido = errores.FirstOrDefault(error => error.Contains("RNC", StringComparison.OrdinalIgnoreCase) || error.Contains("identificación de cliente", StringComparison.OrdinalIgnoreCase));
        var ncfInvalido = errores.FirstOrDefault(error => error.Contains("NCF", StringComparison.OrdinalIgnoreCase));
        var tipoInvalido = errores.FirstOrDefault(error => error.Contains("tipo de ingreso", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("tipo de bienes y servicios", StringComparison.OrdinalIgnoreCase) || error.Contains("tipo fiscal", StringComparison.OrdinalIgnoreCase) || error.Contains("clasificar el tipo", StringComparison.OrdinalIgnoreCase));
        var serviciosInvalidos = errores.FirstOrDefault(error => error.Contains("monto facturado en servicios", StringComparison.OrdinalIgnoreCase));
        var bienesInvalidos = errores.FirstOrDefault(error => error.Contains("monto facturado en bienes", StringComparison.OrdinalIgnoreCase));
        var exentoInvalido = errores.FirstOrDefault(error => error.Contains("monto exento", StringComparison.OrdinalIgnoreCase));
        var montosAusentes = errores.FirstOrDefault(error => error.Contains("montos fiscales numéricos", StringComparison.OrdinalIgnoreCase));
        var gravadoInvalido = errores.FirstOrDefault(error => error.Contains("monto gravado", StringComparison.OrdinalIgnoreCase)) ?? montosAusentes;
        var itbisInvalido = errores.FirstOrDefault(error => error.Contains("ITBIS", StringComparison.OrdinalIgnoreCase)) ?? montosAusentes;
        var totalInvalido = errores.FirstOrDefault(error => error.Contains("total facturado", StringComparison.OrdinalIgnoreCase)) ?? montosAusentes;
        AplicarError(_campoNcf, ncfInvalido is null ? null : "NCF inválido o incompleto");
        AplicarError(_campoRnc, rncInvalido is null ? null : "RNC / cédula inválido");
        AplicarError(_campoRazonSocial, null);
        AplicarError(_campoTipoFiscal, tipoInvalido is null ? null : _tipo == "607" ? "Debe seleccionar un código oficial DGII 01-06" : "Debe seleccionar un código oficial DGII para el Formato 606");
        AplicarError(_campoMontoGravado, gravadoInvalido is null ? null : "Monto gravado inválido");
        AplicarError(_campoItbis, itbisInvalido is null ? null : "ITBIS inválido");
        AplicarError(_campoTotal, totalInvalido is null ? null : "Total inválido");
        AplicarError(_campoMontoServicios, serviciosInvalidos is null ? null : "Monto facturado en servicios inválido");
        AplicarError(_campoMontoBienes, bienesInvalidos is null ? null : "Monto facturado en bienes inválido");
        AplicarError(_campoMontoExento, exentoInvalido is null ? null : "Monto exento inválido");
    }

    private void ValidarCamposEditables(bool recalcularTotal)
    {
        var indice = _registro.SelectedIndex;
        if (_cargandoRegistro || _actualizandoTotal || indice < 0 || indice >= _problemas.Count) return;

        var fiscal = _problemas[indice].Registro;
        var gravadoValido = CorreccionFiscalMontos.TryLeer(_montoGravado.Text, out var gravado);
        var itbisValido = CorreccionFiscalMontos.TryLeer(_itbis.Text, out _);
        AplicarError(_campoRazonSocial, string.IsNullOrWhiteSpace(_razonSocial.Text) ? "La razón social es obligatoria" : null);
        var serviciosValidos = CorreccionFiscalMontos.TryLeer(_montoServicios.Text, out var servicios);
        var bienesValidos = CorreccionFiscalMontos.TryLeer(_montoBienes.Text, out var bienes);
        var exentoValido = CorreccionFiscalMontos.TryLeer(_montoExento.Text, out _);
        var desgloseValido = serviciosValidos && bienesValidos && gravadoValido &&
            servicios <= gravado && bienes == gravado - servicios;
        AplicarError(_campoTipoFiscal, _tipoFiscal.SelectedItem is OpcionFiscal ? null :
            _tipo == "607" ? "Debe seleccionar un código oficial DGII 01-06" : "Debe seleccionar un código oficial DGII para el Formato 606");
        AplicarError(_campoMontoGravado, gravadoValido ? null : "Monto gravado inválido");
        AplicarError(_campoItbis, itbisValido ? null : "ITBIS inválido");
        if (_tipo == "606")
        {
            AplicarError(_campoMontoServicios, !serviciosValidos ? "Monto facturado en servicios inválido" :
                gravadoValido && bienesValidos && !desgloseValido ? "La suma de servicios y bienes debe coincidir con el monto gravado" : null);
            AplicarError(_campoMontoBienes, !bienesValidos ? "Monto facturado en bienes inválido" :
                gravadoValido && serviciosValidos && !desgloseValido ? "La suma de servicios y bienes debe coincidir con el monto gravado" : null);
        }
        AplicarError(_campoMontoExento, exentoValido ? null : "Monto exento inválido");

        var totalCalculable = CorreccionFiscalMontos.TryCalcularTotal(_montoGravado.Text, _itbis.Text,
            _montoExento.Text, fiscal.OtrosImpuestos, fiscal.PropinaLegal, out var esperado);
        if (recalcularTotal && totalCalculable)
        {
            _actualizandoTotal = true;
            try { _total.Text = esperado.ToString("F2", CultureInfo.InvariantCulture); }
            finally { _actualizandoTotal = false; }
        }
        var totalValido = CorreccionFiscalMontos.TryLeer(_total.Text, out var total);
        AplicarError(_campoTotal, !totalValido ? "Total inválido" : !totalCalculable ?
            "No se puede calcular el total: revise el gravado, ITBIS, exento, otros impuestos y propina del registro" :
            total != esperado ? $"El total debe ser {esperado:F2}: gravado + exento + ITBIS + otros impuestos + propina" : null);
    }

    private async void GuardarCorreccion(object? sender, EventArgs e)
    {
        if (_registro.SelectedIndex < 0 || _registro.SelectedIndex >= _problemas.Count) return;
        ValidarCamposEditables(false);
        if (!CorreccionFiscalMontos.TryLeer(_montoGravado.Text, out var gravado) ||
            !CorreccionFiscalMontos.TryLeer(_itbis.Text, out var itbis) ||
            !CorreccionFiscalMontos.TryLeer(_total.Text, out var total))
        {
            _estado.Text = "Introduce montos no negativos con hasta dos decimales, usando punto o coma.";
            return;
        }
        if (_tipoFiscal.SelectedItem is not OpcionFiscal clasificacion)
        {
            _estado.Text = "Selecciona el código fiscal correspondiente.";
            return;
        }
        var fiscalSeleccionado = _problemas[_registro.SelectedIndex].Registro;
        if (CorreccionFiscalMontos.TryCalcularTotal(_montoGravado.Text, _itbis.Text, _montoExento.Text,
            fiscalSeleccionado.OtrosImpuestos, fiscalSeleccionado.PropinaLegal, out var esperado) && total != esperado)
        {
            _estado.Text = "El total no coincide con los importes del registro. Corrígelo o usa Calcular total.";
            return;
        }
        if (!CorreccionFiscalMontos.TryLeer(_montoExento.Text, out var exento))
        {
            _estado.Text = "Introduce un monto exento válido; indica cero solo si corresponde al comprobante.";
            return;
        }
        decimal? servicios = null;
        decimal? bienes = null;
        if (_tipo == "606")
        {
            if (!CorreccionFiscalMontos.TryLeer(_montoServicios.Text, out var valorServicios) ||
                !CorreccionFiscalMontos.TryLeer(_montoBienes.Text, out var valorBienes))
            {
                _estado.Text = "Introduce montos válidos para servicios y bienes, usando punto o coma.";
                return;
            }
            servicios = valorServicios;
            bienes = valorBienes;
            if (valorServicios > gravado || valorBienes != gravado - valorServicios)
            {
                _estado.Text = "El monto facturado en servicios y bienes debe sumar el monto gravado total.";
                AplicarError(_campoMontoServicios, "La suma debe coincidir con el monto gravado");
                AplicarError(_campoMontoBienes, "La suma debe coincidir con el monto gravado");
                return;
            }
        }
        _guardar.IsEnabled = false;
        try
        {
            var fiscal = _problemas[_registro.SelectedIndex].Registro;
            await _db.GuardarCorreccionRegistroFiscalAsync(_usuarioId, _tipo, _periodo.Year, _periodo.Month, fiscal.Id,
                (_rnc.Text ?? string.Empty).Trim(), (_razonSocial.Text ?? string.Empty).Trim(), clasificacion.Codigo, gravado, itbis, total,
                servicios, bienes, exento);
            if (!await CargarProblemasAsync(fiscal.Id))
            {
                _estado.Text = $"La corrección se guardó, pero no se pudo volver a validar. {_estado.Text}";
                return;
            }
            var pendiente = _problemas.FirstOrDefault(problema => problema.Registro.Id == fiscal.Id);
            _estado.Text = pendiente is null
                ? "✅ REGISTRO VALIDADO CORRECTAMENTE, ELIMINADO DE LA LISTA DE ERRORES. Validación local; verifique con la herramienta oficial DGII."
                : "⚠️ Corrección guardada, pero el registro aún presenta errores. Revise la lista superior.";
            await DisplayAlertAsync("Corrección guardada", _estado.Text, "Aceptar");
        }
        catch (Exception ex)
        {
            _estado.Text = $"No se guardó la corrección: {ex.Message}";
        }
        finally
        {
            var indice = _registro.SelectedIndex;
            _guardar.IsEnabled = !_noSePudoVerificarEnvios && indice >= 0 && indice < _problemas.Count &&
                !_registrosEnviados.Contains(_problemas[indice].Registro.Id);
        }
    }

    private async void VolverAValidar(object? sender, EventArgs e)
    {
        _revalidar.IsEnabled = false;
        try
        {
            if (!await CargarProblemasAsync()) return;
            if (_problemas.Count > 0)
            {
                _estado.Text = "⚠️ La validación de los registros guardados encontró errores. Revise la lista superior.";
                return;
            }
            var usuario = _usuarioId;
#if WINDOWS
            var documentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documentos)) throw new InvalidOperationException("No se encontró la carpeta Documentos.");
            var carpeta = Path.Combine(documentos, "DGII Automática", $"Archivos {_tipo}");
#else
            var carpeta = Path.Combine(FileSystem.AppDataDirectory, $"Archivos {_tipo}");
#endif
            var resultado = _tipo == "607"
                ? await _formatoDgii.Generar607Async(usuario, _rncContribuyente, _periodo.Year, _periodo.Month, carpeta)
                : await _formatoDgii.Generar606Async(usuario, _rncContribuyente, _periodo.Year, _periodo.Month, carpeta);
            if (!resultado.Valido)
            {
                if (await CargarProblemasAsync())
                    _estado.Text = $"⚠️ No se generó el archivo {_tipo}. Corrija los registros fiscales que se muestran a continuación:\n" + string.Join(Environment.NewLine, resultado.Errores);
                return;
            }
            await _archivoGenerado(resultado);
            await DisplayAlertAsync("Validación completada", $"✅ Todos los registros pasaron la validación y se generó el borrador {_tipo}: {resultado.Ruta}", "Aceptar");
            await Navigation.PopAsync();
        }
        catch (PlantillaDgiiException ex)
        {
            _estado.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _estado.Text = $"No se pudo volver a validar o generar el archivo {_tipo}: {ex.Message}";
        }
        finally
        {
            _revalidar.IsEnabled = !_noSePudoVerificarEnvios;
        }
    }

    private void HabilitarCampos(bool habilitar)
    {
        _rnc.IsEnabled = habilitar;
        _razonSocial.IsEnabled = habilitar;
        _tipoFiscal.IsEnabled = habilitar;
        _montoGravado.IsEnabled = habilitar;
        _itbis.IsEnabled = habilitar;
        _total.IsEnabled = habilitar;
        _calcularTotal.IsEnabled = habilitar;
        _montoServicios.IsEnabled = habilitar && _tipo == "606";
        _montoBienes.IsEnabled = habilitar && _tipo == "606";
        _montoExento.IsEnabled = habilitar;
    }
}
