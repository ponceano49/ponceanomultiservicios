using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using PonceanoFacturador.Models;

namespace PonceanoFacturador.Services;

public sealed record ResultadoValidacionFormatoDgii(bool Valido, int CantidadRegistros, IReadOnlyList<string> Errores);

public sealed class PlantillaDgiiException(string message, Exception? innerException = null) : IOException(message, innerException);

public sealed class FormatoEnvioDgiiService
{
    // Columnas numeradas de detalle de los XLS incluidos; se excluyen el consecutivo y el estatus calculado.
    private static readonly string[] ColumnasPlantilla606 =
    {
        "RNC o Cédula", "Tipo Id", "Tipo Bienes y Servicios Comprados", "NCF", "NCF ó Documento Modificado",
        "Fecha Comprobante", "Fecha Pago", "Monto Facturado en Servicios", "Monto Facturado en Bienes", "Total Monto Facturado",
        "ITBIS Facturado", "ITBIS Retenido", "ITBIS sujeto a Proporcionalidad (Art. 349)", "ITBIS llevado al Costo",
        "ITBIS por Adelantar", "ITBIS percibido en compras", "Tipo de Retención en ISR", "Monto Retención Renta",
        "ISR Percibido en compras", "Impuesto Selectivo al Consumo", "Otros Impuesto/Tasas", "Monto Propina Legal", "Forma de Pago"
    };
    private static readonly string[] ColumnasPlantilla607 =
    {
        "RNC/Cédula o Pasaporte", "Tipo Identificación", "Número Comprobante Fiscal", "Número Comprobante Fiscal Modificado",
        "Tipo de Ingreso", "Fecha Comprobante", "Fecha de Retención", "Monto Facturado", "ITBIS Facturado",
        "ITBIS Retenido por Terceros", "ITBIS Percibido", "Retención Renta por Terceros", "ISR Percibido",
        "Impuesto Selectivo al Consumo", "Otros Impuestos/Tasas", "Monto Propina Legal", "Efectivo",
        "Cheque/ Transferencia/ Depósito", "Tarjeta Débito/Crédito", "Venta a Crédito", "Bonos o Certificados de Regalo",
        "Permuta", "Otras Formas de Ventas"
    };
    private const string Plantilla606Sha256 = "C858AEC95CC744B75FB6280060341561013AAB812CE434E78E0C3BBEE769C8BF";
    private const string Plantilla607Sha256 = "AF9EED59C1E2B0099F6F60EA33D3034420812E5EC9A5EC63A24879B762986EA7";
    private static readonly Regex NcfRegex = new(@"^(?:[AB][0-9]{10}|E[0-9]{12}|[ACDF-Z][0-9]{19}|[A-Z0-9]{3}-[0-9]{3,4}-[0-9]{7,10})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly DatabaseService _db;

    public FormatoEnvioDgiiService(DatabaseService db) => _db = db;

    public async Task<ResultadoExportacionFiscal> Generar606Async(string usuarioId, string rncContribuyente, int anio, int mes, string carpetaDestino)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("Inicia sesión antes de generar el Formato 606.", nameof(usuarioId));
        if (!RncValido(rncContribuyente)) throw new InvalidOperationException("Introduce el RNC o cédula del contribuyente (9 u 11 dígitos).");
        rncContribuyente = SoloDigitos(rncContribuyente);
        if (anio is < 2000 or > 9998 || mes is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(mes), "Selecciona un período válido.");
        ArgumentException.ThrowIfNullOrWhiteSpace(carpetaDestino);
        await VerificarPlantillaIncluidaAsync("606");

        var inicio = new DateTime(anio, mes, 1, 0, 0, 0, DateTimeKind.Local);
        var registros = await _db.ObtenerRegistrosFiscalesAsync(usuarioId, "Compra", inicio, inicio.AddMonths(1));
        var errores = ValidarRegistros606(registros, inicio);
        if (errores.Count != 0) return new ResultadoExportacionFiscal(false, string.Empty, registros.Count, errores);
        if (registros.Count > 10000) return new ResultadoExportacionFiscal(false, string.Empty, registros.Count, new[] { "El formato 606 admite hasta 10,000 registros por archivo." });

        var ruta = Path.Combine(carpetaDestino, $"606_LibroCompras_{mes:D2}_{anio}.txt");
        Directory.CreateDirectory(carpetaDestino);
        if (File.Exists(ruta)) throw new IOException($"Ya existe el archivo {ruta}. Marca el archivo actual como enviado antes de generar otro para el mismo período.");
        var filas = new List<string> { $"{rncContribuyente}|{anio}{mes:D2}|{registros.Count}" };
        filas.AddRange(registros.Select(CrearFila606));
        if (filas.Skip(1).Any(fila => fila.Split('|').Length != ColumnasPlantilla606.Length))
            throw new InvalidDataException($"La fila generada no coincide con las {ColumnasPlantilla606.Length} columnas numeradas de detalle de la plantilla DGII 606.");
        var contenido = Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, filas) + Environment.NewLine);
        await using (var verificacion = new MemoryStream(contenido, writable: false))
        {
            var validacion = await ValidarArchivoAsync(verificacion, "606", rncContribuyente, anio, mes);
            if (!validacion.Valido) return new ResultadoExportacionFiscal(false, string.Empty, registros.Count, validacion.Errores);
        }
        await File.WriteAllBytesAsync(ruta, contenido);
        try
        {
            await _db.RegistrarExportacionDGIIAsync(new HistorialExportacionDGII
            {
                TipoReporte = "606", PeriodoMes = mes, PeriodoAnio = anio, IdUsuario = usuarioId,
                NombreArchivo = Path.GetFileName(ruta), RutaGuardado = ruta, CantidadRegistros = registros.Count,
                Estado = "Borrador estructurado 606; pendiente de prevalidación oficial DGII",
                ObservacionesDGII = "Mapeado a las 23 columnas numeradas de detalle de la plantilla local DGII 606. Confirmar con la herramienta oficial de prevalidación antes de remitir."
            });
        }
        catch
        {
            File.Delete(ruta);
            throw;
        }
        return new ResultadoExportacionFiscal(true, ruta, registros.Count, Array.Empty<string>());
    }

    public async Task<IReadOnlyList<ProblemaRegistroFiscal>> ObtenerProblemas606Async(string usuarioId, int anio, int mes)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("Inicia sesión antes de validar los registros.", nameof(usuarioId));
        if (anio is < 2000 or > 9998 || mes is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(mes), "Selecciona un período válido.");
        var inicio = new DateTime(anio, mes, 1, 0, 0, 0, DateTimeKind.Local);
        var registros = await _db.ObtenerRegistrosFiscalesAsync(usuarioId, "Compra", inicio, inicio.AddMonths(1));
        return registros.Select(registro => new ProblemaRegistroFiscal(registro, ValidarRegistro606(registro, inicio)))
            .Where(problema => problema.Errores.Count > 0).ToArray();
    }

    public async Task<ResultadoExportacionFiscal> Generar607Async(string usuarioId, string rncContribuyente, int anio, int mes, string carpetaDestino)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("Inicia sesión antes de generar el Formato 607.", nameof(usuarioId));
        if (!RncValido(rncContribuyente)) throw new InvalidOperationException("Introduce el RNC o cédula del contribuyente (9 u 11 dígitos).");
        rncContribuyente = SoloDigitos(rncContribuyente);
        if (anio is < 2000 or > 9998 || mes is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(mes), "Selecciona un período válido.");
        ArgumentException.ThrowIfNullOrWhiteSpace(carpetaDestino);
        await VerificarPlantillaIncluidaAsync("607");

        var inicio = new DateTime(anio, mes, 1, 0, 0, 0, DateTimeKind.Local);
        var registros = await _db.ObtenerRegistrosFiscalesAsync(usuarioId, "Venta", inicio, inicio.AddMonths(1));
        var errores = ValidarRegistros607(registros, inicio);
        if (errores.Count != 0) return new ResultadoExportacionFiscal(false, string.Empty, registros.Count, errores);
        if (registros.Count > 65000) return new ResultadoExportacionFiscal(false, string.Empty, registros.Count, new[] { "El formato oficial 607 admite hasta 65,000 registros por archivo." });

        var ruta = Path.Combine(carpetaDestino, $"607_LibroVentas_{mes:D2}_{anio}.txt");
        Directory.CreateDirectory(carpetaDestino);
        if (File.Exists(ruta)) throw new IOException($"Ya existe el archivo {ruta}. Marca el archivo actual como enviado antes de generar otro para el mismo período.");
        var filas = new List<string> { $"{rncContribuyente.Trim()}|{anio}{mes:D2}|{registros.Count}" };
        filas.AddRange(registros.Select(CrearFila607));
        if (filas.Skip(1).Any(fila => fila.Split('|').Length != ColumnasPlantilla607.Length))
            throw new InvalidDataException($"La fila generada no coincide con las {ColumnasPlantilla607.Length} columnas numeradas de detalle de la plantilla DGII 607.");
        var contenido = Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, filas) + Environment.NewLine);
        await using (var verificacion = new MemoryStream(contenido, writable: false))
        {
            var validacion = await ValidarArchivoAsync(verificacion, "607", rncContribuyente, anio, mes);
            if (!validacion.Valido) return new ResultadoExportacionFiscal(false, string.Empty, registros.Count, validacion.Errores);
        }
        await File.WriteAllBytesAsync(ruta, contenido);
        try
        {
            await _db.RegistrarExportacionDGIIAsync(new HistorialExportacionDGII
            {
                TipoReporte = "607", PeriodoMes = mes, PeriodoAnio = anio, IdUsuario = usuarioId,
                NombreArchivo = Path.GetFileName(ruta), RutaGuardado = ruta, CantidadRegistros = registros.Count,
                Estado = "Borrador estructurado 607; pendiente de prevalidación oficial DGII",
                ObservacionesDGII = "Generado localmente con las columnas publicadas en la plantilla DGII 607. Confirmar con la herramienta oficial de prevalidación antes de remitir."
            });
        }
        catch
        {
            File.Delete(ruta);
            throw;
        }
        return new ResultadoExportacionFiscal(true, ruta, registros.Count, Array.Empty<string>());
    }

    private static async Task VerificarPlantillaIncluidaAsync(string tipo)
    {
        var (nombre, shaEsperado) = tipo switch
        {
            "606" => ("DGIIFormato606.xls", Plantilla606Sha256),
            "607" => ("DGIIFormato607.xls", Plantilla607Sha256),
            _ => throw new ArgumentException("El formato debe ser 606 o 607.", nameof(tipo))
        };
        try
        {
            await using var plantilla = await FileSystem.OpenAppPackageFileAsync(nombre);
            var shaReal = Convert.ToHexString(await SHA256.HashDataAsync(plantilla));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(shaReal), Convert.FromHexString(shaEsperado)))
                throw new PlantillaDgiiException($"❌ Error: No se pudo leer la plantilla DGII {tipo}; verifique la carpeta de plantillas incluida en la aplicación.");
        }
        catch (PlantillaDgiiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            throw new PlantillaDgiiException($"❌ Error: No se pudo leer la plantilla DGII {tipo}; verifique la carpeta de plantillas incluida en la aplicación.", ex);
        }
    }

    public static async Task<ResultadoValidacionFormatoDgii> ValidarArchivoAsync(Stream archivo, string tipo, string rncEsperado, int anio, int mes)
    {
        ArgumentNullException.ThrowIfNull(archivo);
        if (tipo is not ("606" or "607")) throw new ArgumentException("El formato debe ser 606 o 607.", nameof(tipo));
        var errores = new List<string>();
        if (!RncValido(rncEsperado)) errores.Add("El RNC o cédula del contribuyente debe contener 9 u 11 dígitos.");
        rncEsperado = SoloDigitos(rncEsperado);
        if (anio is < 2000 or > 9998 || mes is < 1 or > 12) errores.Add("Selecciona un período válido.");

        using var lector = new StreamReader(archivo, new UTF8Encoding(false, true), true, leaveOpen: true);
        var encabezado = await lector.ReadLineAsync();
        var filas = new List<string>();
        string? linea;
        while ((linea = await lector.ReadLineAsync()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(linea)) filas.Add(linea);
        }
        if (encabezado is null) return new ResultadoValidacionFormatoDgii(false, 0, errores.Append("El archivo está vacío.").ToArray());
        encabezado = encabezado.TrimStart('\uFEFF');
        var camposEncabezado = encabezado.Split('|');
        if (camposEncabezado.Length != 3 || !RncValido(camposEncabezado[0]) ||
            !int.TryParse(camposEncabezado[1], NumberStyles.None, CultureInfo.InvariantCulture, out var periodo) ||
            periodo != anio * 100 + mes || !int.TryParse(camposEncabezado[2], NumberStyles.None, CultureInfo.InvariantCulture, out var cantidad) || cantidad < 0)
        {
            errores.Add("La primera línea debe contener RNC|AAAAMM|CantidadRegistros con datos válidos.");
            return new ResultadoValidacionFormatoDgii(false, filas.Count, errores);
        }
        if (!string.Equals(camposEncabezado[0], rncEsperado.Trim(), StringComparison.Ordinal)) errores.Add("El RNC del encabezado no coincide con el contribuyente seleccionado.");
        var limite = tipo == "606" ? 10000 : 65000;
        if (cantidad > limite) errores.Add($"La cantidad de registros supera el límite de {limite} para el Formato {tipo}.");
        if (cantidad != filas.Count) errores.Add($"El encabezado declara {cantidad} registros, pero el archivo contiene {filas.Count}.");
        if (cantidad == 0) errores.Add("El archivo no contiene comprobantes; para períodos sin operaciones debe usarse la declaración informativa en cero de la OFV.");

        for (var index = 0; index < filas.Count; index++)
        {
            var numero = index + 2;
            var campos = filas[index].Split('|');
            var columnasEsperadas = tipo == "606" ? ColumnasPlantilla606.Length : ColumnasPlantilla607.Length;
            if (campos.Length != columnasEsperadas)
            {
                errores.Add($"Línea {numero}: se esperaban {columnasEsperadas} columnas de detalle del Formato {tipo}.");
                continue;
            }
            if (tipo == "607") ValidarFila607(campos, numero, anio, mes, errores);
            else ValidarFila606(campos, numero, anio, mes, errores);
        }
        return new ResultadoValidacionFormatoDgii(errores.Count == 0, filas.Count, errores);
    }

    public async Task<string> CrearCopiaEnviadaAsync(string usuarioId, string tipo, int anio, int mes, string rnc, Stream origen, string carpetaDestino, string nombreOriginal, int cantidadRegistros)
    {
        ArgumentNullException.ThrowIfNull(origen);
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        if (tipo is not ("606" or "607")) throw new ArgumentException("El formato debe ser 606 o 607.", nameof(tipo));
        if (!RncValido(rnc)) throw new ArgumentException("El RNC o cédula del contribuyente debe contener 9 u 11 dígitos.", nameof(rnc));
        if (anio is < 2000 or > 9998 || mes is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(mes), "Selecciona un período válido.");
        if (cantidadRegistros <= 0) throw new ArgumentOutOfRangeException(nameof(cantidadRegistros));
        ArgumentException.ThrowIfNullOrWhiteSpace(carpetaDestino);
        Directory.CreateDirectory(carpetaDestino);
        var ahora = DateTime.Now;
        var libro = tipo == "607" ? "LibroVentas" : "LibroCompras";
        var nombre = $"{tipo}_ENVIADO_{libro}_{mes:D2}_{anio}_{ahora:yyyyMMdd_HHmmss_fff}.txt";
        var ruta = Path.Combine(carpetaDestino, nombre);
        try
        {
            await using (var salida = new FileStream(ruta, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await origen.CopyToAsync(salida);
                await salida.FlushAsync();
            }
            await _db.RegistrarExportacionDGIIAsync(new HistorialExportacionDGII
            {
                TipoReporte = tipo, PeriodoMes = mes, PeriodoAnio = anio, IdUsuario = usuarioId,
                NombreArchivo = nombre, RutaGuardado = ruta, CantidadRegistros = cantidadRegistros,
                Estado = "Copia archivada tras confirmación manual de envío por el usuario",
                ObservacionesDGII = $"Archivo seleccionado: {Path.GetFileName(nombreOriginal)}. RNC reportado: {rnc}. Fecha de confirmación local: {ahora:O}. El usuario confirmó manualmente el envío y recepción del acuse en la OFV."
            });
        }
        catch
        {
            if (File.Exists(ruta)) File.Delete(ruta);
            throw;
        }
        return ruta;
    }

    public async Task<IReadOnlyList<ProblemaRegistroFiscal>> ObtenerProblemas607Async(string usuarioId, int anio, int mes)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("Inicia sesión antes de validar los registros.", nameof(usuarioId));
        if (anio is < 2000 or > 9998 || mes is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(mes), "Selecciona un período válido.");
        var inicio = new DateTime(anio, mes, 1, 0, 0, 0, DateTimeKind.Local);
        var registros = await _db.ObtenerRegistrosFiscalesAsync(usuarioId, "Venta", inicio, inicio.AddMonths(1));
        return registros.Select(registro => new ProblemaRegistroFiscal(registro, ValidarRegistros607(new[] { registro }, inicio)))
            .Where(problema => problema.Errores.Count > 0).ToArray();
    }

    private static List<string> ValidarRegistros606(IReadOnlyCollection<RegistroFiscal> registros, DateTime inicio)
    {
        var errores = new List<string>();
        if (registros.Count == 0) errores.Add("No hay registros fiscales de compras para el período.");
        foreach (var registro in registros) errores.AddRange(ValidarRegistro606(registro, inicio));
        return errores;
    }

    internal static IReadOnlyList<string> ValidarRegistro(RegistroFiscal registro, string tipo, DateTime inicio) => tipo switch
    {
        "606" => ValidarRegistro606(registro, inicio),
        "607" => ValidarRegistros607(new[] { registro }, inicio),
        _ => throw new ArgumentException("El formato debe ser 606 o 607.", nameof(tipo))
    };

    private static List<string> ValidarRegistro606(RegistroFiscal registro, DateTime inicio)
    {
        var errores = new List<string>();
        var etiqueta = string.IsNullOrWhiteSpace(registro.Ncf) ? registro.Id : registro.Ncf;
        var fecha = registro.FechaComprobante.ToLocalTime();
        if (!RncValido(registro.RncContraparte)) errores.Add($"{etiqueta}: el RNC o cédula del proveedor debe contener 9 u 11 dígitos.");
        if (string.IsNullOrWhiteSpace(registro.RazonSocialContraparte)) errores.Add($"{etiqueta}: falta la razón social del proveedor.");
        if (!Regex.IsMatch(registro.TipoBienServicio?.Trim() ?? string.Empty, @"^(?:0[1-9]|1[01])$"))
            errores.Add($"{etiqueta}: el tipo de bienes y servicios debe ser un código oficial 01-11.");
        if (!NcfRegex.IsMatch(registro.Ncf.Trim())) errores.Add($"{etiqueta}: falta un NCF válido.");
        if (!string.IsNullOrWhiteSpace(registro.NcfModificado) && !NcfRegex.IsMatch(registro.NcfModificado.Trim()))
            errores.Add($"{etiqueta}: el NCF modificado no es válido.");
        if (fecha < inicio || fecha >= inicio.AddMonths(1)) errores.Add($"{etiqueta}: la fecha no pertenece al período seleccionado.");

        var serviciosOk = LeerMonto(registro.MontoGravadoServicios, etiqueta, "monto facturado en servicios", errores, out var servicios);
        var bienesOk = LeerMonto(registro.MontoGravadoBienes, etiqueta, "monto facturado en bienes", errores, out var bienes);
        var exentoOk = LeerMonto(registro.MontoExento, etiqueta, "monto exento", errores, out var exento);
        var gravadoOk = LeerMonto(registro.MontoGravadoTotal, etiqueta, "monto gravado total", errores, out var gravado);
        if (serviciosOk && bienesOk && gravadoOk && servicios + bienes != gravado)
            errores.Add($"{etiqueta}: las bases gravadas de bienes y servicios no coinciden con el monto gravado total.");
        LeerMontoOpcional(registro.ItbisFacturado, etiqueta, "ITBIS facturado", errores);
        LeerMontoOpcional(registro.ItbisRetenido, etiqueta, "ITBIS retenido", errores);
        LeerMontoOpcional(registro.OtrosImpuestos, etiqueta, "otros impuestos", errores);
        LeerMontoOpcional(registro.PropinaLegal, etiqueta, "propina legal", errores);
        LeerMontoOpcional(registro.IsrRetenido, etiqueta, "retención ISR", errores);
        if (decimal.TryParse(registro.IsrRetenido, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var isrRetenido) &&
            isrRetenido > 0)
            errores.Add($"{etiqueta}: falta el código de tipo de retención ISR requerido por la plantilla 606.");
        if (!TipoPago606Valido(registro.MetodoPago)) errores.Add($"{etiqueta}: el método de pago no se puede mapear a un código oficial 01-07.");
        if (serviciosOk && bienesOk && exentoOk && gravadoOk && gravado + exento <= 0)
            errores.Add($"{etiqueta}: el monto facturado debe ser mayor que cero.");
        return errores;
    }

    private static List<string> ValidarRegistros607(IReadOnlyCollection<RegistroFiscal> registros, DateTime inicio)
    {
        var errores = new List<string>();
        if (registros.Count == 0) errores.Add("No hay registros fiscales de ventas para el período. Las capturas manuales DGII no se incluyen en el libro fiscal.");
        foreach (var registro in registros)
        {
            var etiqueta = string.IsNullOrWhiteSpace(registro.Ncf) ? registro.Id : registro.Ncf;
            var fecha = registro.FechaComprobante.ToLocalTime();
            if (!RncValido(registro.RncContraparte)) errores.Add($"{etiqueta}: el RNC/cédula del cliente debe contener 9 u 11 dígitos.");
            if (string.IsNullOrWhiteSpace(registro.RazonSocialContraparte)) errores.Add($"{etiqueta}: falta la razón social del cliente.");
            if (!NcfRegex.IsMatch(registro.Ncf.Trim())) errores.Add($"{etiqueta}: falta un NCF válido.");
            if (!string.IsNullOrWhiteSpace(registro.NcfModificado) && !NcfRegex.IsMatch(registro.NcfModificado.Trim()))
                errores.Add($"{etiqueta}: el NCF modificado no es válido.");
            if (fecha < inicio || fecha >= inicio.AddMonths(1)) errores.Add($"{etiqueta}: la fecha no pertenece al período seleccionado.");
            if (!TipoIngresoValido(registro.TipoIngresoGasto)) errores.Add($"{etiqueta}: el tipo de ingreso debe clasificarse con un código oficial 01-06.");
            var gravadoOk = LeerMonto(registro.MontoGravadoTotal, etiqueta, "monto gravado", errores, out var gravado);
            var exentoOk = LeerMonto(registro.MontoExento, etiqueta, "monto exento", errores, out var exento);
            var totalOk = LeerMonto(registro.TotalFacturado, etiqueta, "total facturado", errores, out var total);
            var itbis = LeerMontoOpcional(registro.ItbisFacturado, etiqueta, "ITBIS", errores);
            var otros = LeerMontoOpcional(registro.OtrosImpuestos, etiqueta, "otros impuestos", errores);
            var propina = LeerMontoOpcional(registro.PropinaLegal, etiqueta, "propina legal", errores);
            var isrRetenido = LeerMontoOpcional(registro.IsrRetenido, etiqueta, "ISR retenido", errores);
            var itbisRetenido = LeerMontoOpcional(registro.ItbisRetenido, etiqueta, "ITBIS retenido", errores);
            if (totalOk && !TipoPago607Valido(registro, total)) errores.Add($"{etiqueta}: la forma y el monto de pago no se pueden representar con los datos guardados; revise Método de Pago y Estado de Pago.");
            if (gravadoOk && exentoOk && totalOk && total != gravado + exento + itbis + otros + propina)
                errores.Add($"{etiqueta}: total facturado no coincide con base gravada + exenta + ITBIS + otros impuestos + propina.");
            if (itbisRetenido > 0 || isrRetenido > 0)
                errores.Add($"{etiqueta}: el registro tiene retenciones, pero el sistema no almacena la fecha de retención exigida por el Formato 607.");
        }
        return errores;
    }

    private static string CrearFila606(RegistroFiscal registro)
    {
        var servicios = Monto(registro.MontoGravadoServicios);
        var bienes = Monto(registro.MontoGravadoBienes);
        var exento = Monto(registro.MontoExento);
        var fecha = registro.FechaComprobante.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var campos = new[]
        {
            SoloDigitos(registro.RncContraparte), SoloDigitos(registro.RncContraparte).Length == 9 ? "1" : "2",
            Campo(registro.TipoBienServicio), Campo(registro.Ncf), Campo(registro.NcfModificado), fecha, string.Empty,
            Formatear(servicios), Formatear(bienes), Formatear(servicios + bienes + exento),
            CampoMontoOpcional(registro.ItbisFacturado), CampoMontoOpcional(registro.ItbisRetenido),
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            CampoMontoOpcional(registro.IsrRetenido), string.Empty, string.Empty, CampoMontoOpcional(registro.OtrosImpuestos),
            CampoMontoOpcional(registro.PropinaLegal), TipoPago606Codigo(registro.MetodoPago)
        };
        return string.Join('|', campos);
    }

    private static string CrearFila607(RegistroFiscal registro)
    {
        var total = Monto(registro.TotalFacturado);
        var formaPago = ClasificarPago(registro, total);
        var fecha = registro.FechaComprobante.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var campos = new[]
        {
            SoloDigitos(registro.RncContraparte), SoloDigitos(registro.RncContraparte).Length == 9 ? "1" : "2",
            Campo(registro.Ncf), Campo(registro.NcfModificado), TipoIngresoCodigo(registro.TipoIngresoGasto), fecha, string.Empty,
            Formatear(total), CampoMontoOpcional(registro.ItbisFacturado),
            CampoMontoOpcional(registro.ItbisRetenido), string.Empty, CampoMontoOpcional(registro.IsrRetenido), string.Empty, string.Empty,
            CampoMontoOpcional(registro.OtrosImpuestos), CampoMontoOpcional(registro.PropinaLegal),
            Formatear(formaPago.Efectivo), Formatear(formaPago.Transferencia), Formatear(formaPago.Tarjeta),
            Formatear(formaPago.Credito), "0.00", "0.00", Formatear(formaPago.Otros)
        };
        return string.Join('|', campos);
    }

    private static void ValidarFila607(string[] c, int linea, int anio, int mes, List<string> errores)
    {
        var etiqueta = $"Línea {linea}";
        var id = SoloDigitos(c[0]);
        if (id.Length is not (9 or 11) || (id.Length == 9 ? c[1] != "1" : c[1] != "2")) errores.Add($"{etiqueta}: identificación de cliente o tipo de identificación no válido.");
        if (!NcfRegex.IsMatch(c[2])) errores.Add($"{etiqueta}: NCF inválido.");
        if (!string.IsNullOrEmpty(c[3]) && !NcfRegex.IsMatch(c[3])) errores.Add($"{etiqueta}: NCF modificado inválido.");
        if (!Regex.IsMatch(c[4], "^0[1-6]$")) errores.Add($"{etiqueta}: tipo de ingreso debe ser código 01-06.");
        if (!FechaValida(c[5], anio, mes, false)) errores.Add($"{etiqueta}: fecha de comprobante inválida o fuera del período.");
        if (!FechaValida(c[6], anio, mes, true)) errores.Add($"{etiqueta}: fecha de retención inválida.");
        var montos = new decimal[16];
        for (var i = 0; i < montos.Length; i++)
        {
            var campo = c[7 + i];
            if (string.IsNullOrWhiteSpace(campo) && i != 0) continue;
            if (!decimal.TryParse(campo, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out montos[i]) || montos[i] < 0 || decimal.Round(montos[i], 2) != montos[i])
                errores.Add($"{etiqueta}: monto inválido en columna {i + 8}.");
        }
        if ((montos[2] > 0 || montos[4] > 0) && string.IsNullOrEmpty(c[6]))
            errores.Add($"{etiqueta}: la fecha de retención es obligatoria cuando hay retenciones de ITBIS o renta.");
        var totalPago = montos.Skip(9).Take(7).Sum();
        if (montos[0] <= 0) errores.Add($"{etiqueta}: el monto facturado debe ser mayor que cero.");
        if (totalPago != montos[0]) errores.Add($"{etiqueta}: las formas de pago deben sumar el monto facturado.");
    }

    private static void ValidarFila606(string[] c, int linea, int anio, int mes, List<string> errores)
    {
        var etiqueta = $"Línea {linea}";
        var id = SoloDigitos(c[0]);
        if (id.Length is not (9 or 11) || (id.Length == 9 ? c[1] != "1" : c[1] != "2")) errores.Add($"{etiqueta}: RNC/cédula o tipo de identificación inválido.");
        if (!Regex.IsMatch(c[2], "^0[1-9]$|^1[01]$")) errores.Add($"{etiqueta}: tipo de bienes y servicios inválido.");
        if (!NcfRegex.IsMatch(c[3])) errores.Add($"{etiqueta}: NCF inválido.");
        if (!string.IsNullOrEmpty(c[4]) && !NcfRegex.IsMatch(c[4])) errores.Add($"{etiqueta}: NCF modificado inválido.");
        if (!FechaValida(c[5], anio, mes, false)) errores.Add($"{etiqueta}: fecha de comprobante inválida o fuera del período.");
        if (!FechaValida(c[6], anio, mes, true)) errores.Add($"{etiqueta}: fecha de pago inválida.");
        var montos = new decimal[14];
        for (var i = 0; i < montos.Length; i++)
        {
            var columna = i < 9 ? i + 7 : i + 8;
            var campo = c[columna];
            if (string.IsNullOrWhiteSpace(campo) && columna != 9) continue;
            if (!decimal.TryParse(campo, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out montos[i]) || montos[i] < 0 || decimal.Round(montos[i], 2) != montos[i])
                errores.Add($"{etiqueta}: monto inválido en columna {columna + 1}.");
        }
        if (montos[2] <= 0) errores.Add($"{etiqueta}: el total facturado debe ser mayor que cero.");
        if (!string.IsNullOrEmpty(c[16]) && !Regex.IsMatch(c[16], "^[0-9]{2}$")) errores.Add($"{etiqueta}: tipo de retención ISR inválido.");
        if (montos[9] > 0 && string.IsNullOrEmpty(c[16])) errores.Add($"{etiqueta}: indique el tipo de retención ISR.");
        if (!Regex.IsMatch(c[22], "^0[1-7]$")) errores.Add($"{etiqueta}: forma de pago inválida.");
    }

    private static string TipoPago606Codigo(string? metodoPago)
    {
        var metodo = (metodoPago ?? string.Empty).Trim();
        if (metodo.Equals("Efectivo", StringComparison.OrdinalIgnoreCase)) return "01";
        if (metodo.Equals("Cheque", StringComparison.OrdinalIgnoreCase) || metodo.Equals("Transferencia", StringComparison.OrdinalIgnoreCase) ||
            metodo.Equals("Depósito", StringComparison.OrdinalIgnoreCase) || metodo.Equals("Deposito", StringComparison.OrdinalIgnoreCase)) return "02";
        if (metodo.Equals("Tarjeta", StringComparison.OrdinalIgnoreCase)) return "03";
        if (metodo.Equals("Crédito", StringComparison.OrdinalIgnoreCase) || metodo.Equals("Credito", StringComparison.OrdinalIgnoreCase)) return "04";
        if (metodo.Equals("Permuta", StringComparison.OrdinalIgnoreCase)) return "06";
        if (metodo.Equals("Otro", StringComparison.OrdinalIgnoreCase)) return "07";
        return string.Empty;
    }

    private static bool TipoPago606Valido(string? metodoPago) => TipoPago606Codigo(metodoPago) is not "";

    private static string CampoMontoOpcional(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return string.Empty;
        return Formatear(decimal.Parse(texto, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
    }

    private static bool RncValido(string? texto)
    {
        var valor = (texto ?? string.Empty).Trim();
        return valor.Length > 0 && valor.All(caracter => char.IsAsciiDigit(caracter) || caracter == '-' || char.IsWhiteSpace(caracter)) && SoloDigitos(valor).Length is 9 or 11;
    }

    private static string SoloDigitos(string? texto) => new((texto ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
    private static bool TipoIngresoValido(string? texto) => TipoIngresoCodigo(texto) is not "";
    private static string TipoIngresoCodigo(string? texto)
    {
        var valor = (texto ?? string.Empty).Trim();
        if (Regex.IsMatch(valor, "^0[1-6]$")) return valor;
        if (valor.Contains("venta de bienes y servicios", StringComparison.OrdinalIgnoreCase)) return "01";
        if (valor.Contains("financier", StringComparison.OrdinalIgnoreCase)) return "02";
        if (valor.Contains("extraordin", StringComparison.OrdinalIgnoreCase)) return "03";
        if (valor.Contains("arrend", StringComparison.OrdinalIgnoreCase)) return "04";
        if (valor.Contains("activo depreciable", StringComparison.OrdinalIgnoreCase)) return "05";
        if (valor.Contains("otros ingresos", StringComparison.OrdinalIgnoreCase)) return "06";
        return string.Empty;
    }
    private static bool TipoPago607Valido(RegistroFiscal registro, decimal total)
    {
        var pagos = ClasificarPago(registro, total);
        return pagos.Efectivo + pagos.Transferencia + pagos.Tarjeta + pagos.Credito + pagos.Otros == total && total > 0;
    }
    private static (decimal Efectivo, decimal Transferencia, decimal Tarjeta, decimal Credito, decimal Otros) ClasificarPago(RegistroFiscal registro, decimal total)
    {
        var estado = registro.CondicionPago.Trim();
        var metodo = registro.MetodoPago.Trim();
        if (estado.Contains("pendiente", StringComparison.OrdinalIgnoreCase) || estado.Contains("crédito", StringComparison.OrdinalIgnoreCase) || estado.Contains("credito", StringComparison.OrdinalIgnoreCase))
            return (0, 0, 0, total, 0);
        if (metodo.Contains("efectivo", StringComparison.OrdinalIgnoreCase)) return (total, 0, 0, 0, 0);
        if (metodo.Contains("transferencia", StringComparison.OrdinalIgnoreCase) || metodo.Contains("cheque", StringComparison.OrdinalIgnoreCase) || metodo.Contains("depósito", StringComparison.OrdinalIgnoreCase) || metodo.Contains("deposito", StringComparison.OrdinalIgnoreCase)) return (0, total, 0, 0, 0);
        if (metodo.Contains("tarjeta", StringComparison.OrdinalIgnoreCase)) return (0, 0, total, 0, 0);
        if (metodo.Contains("otro", StringComparison.OrdinalIgnoreCase)) return (0, 0, 0, 0, total);
        return (0, 0, 0, 0, 0);
    }
    private static bool LeerMonto(string texto, string etiqueta, string campo, List<string> errores, out decimal monto)
    {
        if (!decimal.TryParse(texto, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out monto) || monto < 0 || decimal.Round(monto, 2) != monto)
        {
            errores.Add($"{etiqueta}: falta un {campo} numérico válido.");
            return false;
        }
        return true;
    }
    private static decimal LeerMontoOpcional(string texto, string etiqueta, string campo, List<string> errores)
    {
        if (string.IsNullOrWhiteSpace(texto)) return 0;
        if (decimal.TryParse(texto, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var monto) && monto >= 0 && decimal.Round(monto, 2) == monto) return monto;
        errores.Add($"{etiqueta}: {campo} no es un monto válido.");
        return 0;
    }
    private static decimal Monto(string texto) => decimal.Parse(texto, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    private static string Formatear(decimal monto) => monto.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Campo(string? texto) => (texto ?? string.Empty).Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim().ToUpperInvariant();
    private static bool FechaValida(string texto, int anio, int mes, bool opcional) =>
        string.IsNullOrWhiteSpace(texto) ? opcional : DateTime.TryParseExact(texto, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha) &&
            (opcional || fecha.Year == anio && fecha.Month == mes);
}
