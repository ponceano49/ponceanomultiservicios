using PonceanoFacturador.Models;
using SQLite;

namespace PonceanoFacturador.Services;

public class DatabaseService
{
    private readonly SQLiteAsyncConnection _db;
    private readonly string _dbPath;
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _clientesLock = new(1, 1);
    private readonly SemaphoreSlim _documentosLock = new(1, 1);
    private bool _initialized;
    private bool _clientesInitialized;
    private bool _documentosInitialized;
    private LicenciaRemotaSesion? _licenciaRemota;
    private sealed record LicenciaRemotaSesion(string UsuarioId, DateTime VencimientoUtc, bool PriorizarRemota, Func<bool> SesionActiva);

    public void LimpiarLicenciaRemota() => Volatile.Write(ref _licenciaRemota, null);

    public void EstablecerLicenciaRemota(string usuarioId, DateTime vencimientoUtc, bool priorizarRemota, Func<bool> sesionActiva) =>
        Volatile.Write(ref _licenciaRemota, new LicenciaRemotaSesion(usuarioId, vencimientoUtc, priorizarRemota, sesionActiva));

    private static bool LicenciaRemotaActiva(LicenciaRemotaSesion? remota, string usuarioId) => remota is not null &&
        remota.UsuarioId == usuarioId && remota.VencimientoUtc > DateTime.UtcNow && remota.SesionActiva();

    private bool LicenciaRemotaActiva(string usuarioId) => LicenciaRemotaActiva(Volatile.Read(ref _licenciaRemota), usuarioId);

    public DatabaseService() : this(Path.Combine(FileSystem.AppDataDirectory, "PonceanoFacturador.db3"), null) { }

    public DatabaseService(string dbPath) : this(dbPath, null) { }

    public DatabaseService(string dbPath, Func<DateTime>? clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _now = clock ?? (() => DateTime.Now);
        _db = new SQLiteAsyncConnection(dbPath);
    }

    public async Task InitializeAsync()
    {
        await _initializationLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await _db.RunInTransactionAsync(db =>
            {
                var oldTable = db.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Usuario'") != 0;
                var legacyTable = db.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Usuario_Legado'") != 0;
                if (oldTable && legacyTable)
                    throw new InvalidOperationException("Existen Usuario y Usuario_Legado; se requiere una migración manual para conservar ambas tablas.");
                if (oldTable)
                    db.Execute("ALTER TABLE \"Usuario\" RENAME TO \"Usuario_Legado\"");
                db.CreateTable<UsuarioAutenticado>();
                db.CreateTable<NotaCredito>();
                db.CreateTable<NotaDebito>();
            });
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task SaveUserAsync(UsuarioAutenticado user)
    {
        await InitializeAsync();
        await _db.RunInTransactionAsync(db =>
        {
            var existing = db.Table<UsuarioAutenticado>().FirstOrDefault(u => u.IdFirebase == user.IdFirebase);
            if (existing == null)
                db.Insert(user);
            else
            {
                user.Id = existing.Id;
                db.Update(user);
            }
        });
    }

    public async Task InitAsync()
    {
        await _clientesLock.WaitAsync();
        try
        {
            if (_clientesInitialized) return;
            await _db.RunInTransactionAsync(db =>
            {
                db.CreateTable<NotaCredito>();
                var exists = db.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Cliente'") != 0;
                if (exists)
                {
                    var columns = db.GetTableInfo("Cliente");
                    var idType = db.ExecuteScalar<string>("SELECT type FROM pragma_table_info('Cliente') WHERE name = 'Id'");
                    if (idType == null || !idType.StartsWith("varchar", StringComparison.OrdinalIgnoreCase) ||
                        !columns.Any(c => c.Name == "RncCedula"))
                        throw new InvalidOperationException("La tabla de clientes existente usa un formato anterior. Se requiere una migración manual antes de continuar; no se modificaron los datos.");
                }
                db.CreateTable<Cliente>();
                db.CreateTable<NotaCredito>();
            });
            _clientesInitialized = true;
        }
        finally
        {
            _clientesLock.Release();
        }
    }

    public async Task<List<Cliente>> GetAllClientesAsync()
    {
        await InitAsync();
        return await _db.QueryAsync<Cliente>("SELECT * FROM Cliente ORDER BY Nombre COLLATE NOCASE, Id");
    }

    public async Task<List<Cliente>> BuscarClientesAsync(string texto)
    {
        await InitAsync();
        texto = texto?.Trim() ?? string.Empty;
        return await _db.QueryAsync<Cliente>(
            "SELECT * FROM Cliente WHERE instr(lower(Nombre), lower(?)) > 0 OR instr(RncCedula, ?) > 0 ORDER BY Nombre COLLATE NOCASE, Id", texto, texto);
    }

    public async Task<Cliente?> GetClienteByIdAsync(Guid id)
    {
        await InitAsync();
        return await _db.FindAsync<Cliente>(id);
    }

    public async Task GuardarClienteAsync(Cliente cliente)
    {
        ArgumentNullException.ThrowIfNull(cliente);
        await InitAsync();
        await _db.RunInTransactionAsync(db =>
        {
            if (cliente.Id == Guid.Empty)
            {
                cliente.Id = Guid.NewGuid();
                cliente.FechaCreacion = DateTime.UtcNow;
                db.Insert(cliente);
            }
            else
            {
                var existing = db.Find<Cliente>(cliente.Id)
                    ?? throw new InvalidOperationException("El cliente ya no existe. Vuelve a la lista y actualízala.");
                cliente.FechaCreacion = existing.FechaCreacion;
                db.Update(cliente);
            }
        });
    }

    public async Task<int> EliminarClienteAsync(Guid id)
    {
        await InitAsync();
        return await _db.DeleteAsync<Cliente>(id);
    }

    public async Task InitTablasDocumentosAsync()
    {
        await _documentosLock.WaitAsync();
        try
        {
            if (_documentosInitialized) return;
            await InitializeAsync();
            await InitAsync();
            await _db.RunInTransactionAsync(db =>
            {
                PrepararTablaDocumento<Cotizacion>(db, "Cotizacion", "Cotizacion_Legado", new[] { "ClienteId", "UsuarioId", "ItemsJson", "Itbis", "ValidezHasta" });
                var columnasCotizacion = db.GetTableInfo("Cotizacion");
                if (!columnasCotizacion.Any(columna => columna.Name == "Enviada")) db.Execute("ALTER TABLE Cotizacion ADD COLUMN Enviada INTEGER NOT NULL DEFAULT 0");
                if (!columnasCotizacion.Any(columna => columna.Name == "Pagada")) db.Execute("ALTER TABLE Cotizacion ADD COLUMN Pagada INTEGER NOT NULL DEFAULT 0");
                if (!columnasCotizacion.Any(columna => columna.Name == "Nota")) db.Execute("ALTER TABLE Cotizacion ADD COLUMN Nota TEXT NOT NULL DEFAULT ''");
                if (!columnasCotizacion.Any(columna => columna.Name == "FirmaEmpresaRuta")) db.Execute("ALTER TABLE Cotizacion ADD COLUMN FirmaEmpresaRuta TEXT NOT NULL DEFAULT ''");
                if (!columnasCotizacion.Any(columna => columna.Name == "FirmaClienteRuta")) db.Execute("ALTER TABLE Cotizacion ADD COLUMN FirmaClienteRuta TEXT NOT NULL DEFAULT ''");
                PrepararTablaDocumento<Factura>(db, "Factura", "Factura_Legado", new[] { "NCF", "TipoNCF", "ClienteId", "ItemsJson", "UsuarioId", "Estado" });
                QuitarIndiceUnicoNcf(db);
                var columnasFactura = db.GetTableInfo("Factura");
                if (!columnasFactura.Any(columna => columna.Name == "TipoOperacion")) db.Execute("ALTER TABLE Factura ADD COLUMN TipoOperacion TEXT NOT NULL DEFAULT 'Venta'");
                if (!columnasFactura.Any(columna => columna.Name == "MontoOriginal")) db.Execute("ALTER TABLE Factura ADD COLUMN MontoOriginal TEXT NOT NULL DEFAULT '0.00'");
                if (!columnasFactura.Any(columna => columna.Name == "ItbisOriginal")) db.Execute("ALTER TABLE Factura ADD COLUMN ItbisOriginal TEXT NOT NULL DEFAULT '0.00'");
                if (!columnasFactura.Any(columna => columna.Name == "MonedaOriginal")) db.Execute("ALTER TABLE Factura ADD COLUMN MonedaOriginal TEXT NOT NULL DEFAULT 'DOP'");
                if (!columnasFactura.Any(columna => columna.Name == "TasaCambio")) db.Execute("ALTER TABLE Factura ADD COLUMN TasaCambio TEXT NOT NULL DEFAULT '0.0000'");
                if (!columnasFactura.Any(columna => columna.Name == "RutaArchivoOrigen")) db.Execute("ALTER TABLE Factura ADD COLUMN RutaArchivoOrigen TEXT NOT NULL DEFAULT ''");
                if (!columnasFactura.Any(columna => columna.Name == "BorradorFiscalJson")) db.Execute("ALTER TABLE Factura ADD COLUMN BorradorFiscalJson TEXT NOT NULL DEFAULT ''");
                if (!columnasFactura.Any(columna => columna.Name == "ClasificacionOcrSupuesta")) db.Execute("ALTER TABLE Factura ADD COLUMN ClasificacionOcrSupuesta INTEGER NOT NULL DEFAULT 0");
                if (!columnasFactura.Any(columna => columna.Name == "RecargoPorcentaje")) db.Execute("ALTER TABLE Factura ADD COLUMN RecargoPorcentaje TEXT NOT NULL DEFAULT '0.00'");
                if (!columnasFactura.Any(columna => columna.Name == "RecargoMonto")) db.Execute("ALTER TABLE Factura ADD COLUMN RecargoMonto TEXT NOT NULL DEFAULT '0.00'");
                if (!columnasFactura.Any(columna => columna.Name == "InstruccionPago")) db.Execute("ALTER TABLE Factura ADD COLUMN InstruccionPago TEXT NOT NULL DEFAULT ''");
                if (!columnasFactura.Any(columna => columna.Name == "EstadoPago")) db.Execute("ALTER TABLE Factura ADD COLUMN EstadoPago TEXT NOT NULL DEFAULT 'Pendiente'");
                if (!columnasFactura.Any(columna => columna.Name == "MetodoPago")) db.Execute("ALTER TABLE Factura ADD COLUMN MetodoPago TEXT NOT NULL DEFAULT ''");
                if (!columnasFactura.Any(columna => columna.Name == "VenceFecha")) db.Execute("ALTER TABLE Factura ADD COLUMN VenceFecha TEXT NULL");
                if (!columnasFactura.Any(columna => columna.Name == "Enviada")) db.Execute("ALTER TABLE Factura ADD COLUMN Enviada INTEGER NOT NULL DEFAULT 0");
                if (!columnasFactura.Any(columna => columna.Name == "FirmaClienteRuta")) db.Execute("ALTER TABLE Factura ADD COLUMN FirmaClienteRuta TEXT NOT NULL DEFAULT ''");
                if (!columnasFactura.Any(columna => columna.Name == "VencimientoEliminado")) db.Execute("ALTER TABLE Factura ADD COLUMN VencimientoEliminado INTEGER NOT NULL DEFAULT 0");
                if (!columnasFactura.Any(columna => columna.Name == "Nota")) db.Execute("ALTER TABLE Factura ADD COLUMN Nota TEXT NOT NULL DEFAULT ''");
                if (!columnasFactura.Any(columna => columna.Name == "FirmaEmpresaRuta")) db.Execute("ALTER TABLE Factura ADD COLUMN FirmaEmpresaRuta TEXT NOT NULL DEFAULT ''");
                if (!columnasFactura.Any(columna => columna.Name == "SolicitarFirmaCliente")) db.Execute("ALTER TABLE Factura ADD COLUMN SolicitarFirmaCliente INTEGER NOT NULL DEFAULT 0");
                if (!columnasFactura.Any(columna => columna.Name == "TurnoId")) db.Execute("ALTER TABLE Factura ADD COLUMN TurnoId TEXT NOT NULL DEFAULT ''");
                db.CreateTable<ConfiguracionUsuario>();
                var columnasLicencia = db.GetTableInfo("ConfiguracionUsuario");
                if (!columnasLicencia.Any(columna => columna.Name == "FacturasEsteAnio")) db.Execute("ALTER TABLE ConfiguracionUsuario ADD COLUMN FacturasEsteAnio INTEGER NOT NULL DEFAULT 0");
                if (!columnasLicencia.Any(columna => columna.Name == "UltimoAnioConteo")) db.Execute("ALTER TABLE ConfiguracionUsuario ADD COLUMN UltimoAnioConteo TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'");
                if (!columnasLicencia.Any(columna => columna.Name == "FechaInicioPruebaUtc")) db.Execute("ALTER TABLE ConfiguracionUsuario ADD COLUMN FechaInicioPruebaUtc TEXT NULL");
                if (!columnasLicencia.Any(columna => columna.Name == "FechaVencimientoPruebaUtc")) db.Execute("ALTER TABLE ConfiguracionUsuario ADD COLUMN FechaVencimientoPruebaUtc TEXT NULL");
                if (!columnasLicencia.Any(columna => columna.Name == "IdentificadorHardware")) db.Execute("ALTER TABLE ConfiguracionUsuario ADD COLUMN IdentificadorHardware TEXT NOT NULL DEFAULT ''");
                db.CreateTable<AjustesUsuario>();
                var columnasAjustes = db.GetTableInfo("AjustesUsuario");
                var columnasAjustesNuevas = new (string Nombre, string Definicion)[]
                {
                    ("PagoEfectivo", "INTEGER NOT NULL DEFAULT 1"), ("PagoTransferencia", "INTEGER NOT NULL DEFAULT 1"),
                    ("PagoCheque", "INTEGER NOT NULL DEFAULT 1"), ("PagoTarjeta", "INTEGER NOT NULL DEFAULT 1"),
                    ("PagoCredito", "INTEGER NOT NULL DEFAULT 0"), ("PagoOtro", "INTEGER NOT NULL DEFAULT 0"),
                    ("MetodoOtro", "TEXT NOT NULL DEFAULT ''"), ("BancoNombre", "TEXT NOT NULL DEFAULT ''"),
                    ("BancoNumeroCuenta", "TEXT NOT NULL DEFAULT ''"), ("BancoTipoCuenta", "TEXT NOT NULL DEFAULT 'Ahorros'"),
                    ("BancoRnc", "TEXT NOT NULL DEFAULT ''"),
                    ("MensajeReferencia", "TEXT NOT NULL DEFAULT 'Indique el número de factura como referencia'"),
                    ("RecargoActivo", "INTEGER NOT NULL DEFAULT 0"), ("RecargoPorcentaje", "TEXT NOT NULL DEFAULT '0.00'"),
                    ("RecargoMotivo", "TEXT NOT NULL DEFAULT ''"), ("StripeActivo", "INTEGER NOT NULL DEFAULT 0"),
                    ("PayPalActivo", "INTEGER NOT NULL DEFAULT 0"), ("PayPalCorreo", "TEXT NOT NULL DEFAULT ''")
                };
                foreach (var (nombre, definicion) in columnasAjustesNuevas)
                    if (!columnasAjustes.Any(columna => columna.Name == nombre)) db.Execute($"ALTER TABLE AjustesUsuario ADD COLUMN \"{nombre}\" {definicion}");
                db.CreateTable<NcfUsuario>();
                db.CreateTable<Articulo>();
                var columnasArticulo = db.GetTableInfo("Articulo");
                if (!columnasArticulo.Any(columna => columna.Name == "Codigo")) db.Execute("ALTER TABLE Articulo ADD COLUMN Codigo TEXT NOT NULL DEFAULT ''");
                if (!columnasArticulo.Any(columna => columna.Name == "Itbis")) db.Execute("ALTER TABLE Articulo ADD COLUMN Itbis TEXT NOT NULL DEFAULT '18.00'");
                if (!columnasArticulo.Any(columna => columna.Name == "StockMinimo")) db.Execute("ALTER TABLE Articulo ADD COLUMN StockMinimo INTEGER NOT NULL DEFAULT 0");
                if (!columnasArticulo.Any(columna => columna.Name == "UltimoCosto")) db.Execute("ALTER TABLE Articulo ADD COLUMN UltimoCosto TEXT NOT NULL DEFAULT '0.00'");
                if (!columnasArticulo.Any(columna => columna.Name == "TipoBienServicio")) db.Execute("ALTER TABLE Articulo ADD COLUMN TipoBienServicio TEXT NOT NULL DEFAULT 'Pendiente'");
                db.CreateTable<TurnoCaja>();
                var columnasTurno = db.GetTableInfo("TurnoCaja");
                foreach (var (nombre, definicion) in new[]
                {
                    ("TotalSistema", "TEXT NOT NULL DEFAULT '0.00'"), ("TotalContado", "TEXT NOT NULL DEFAULT '0.00'"),
                    ("Diferencia", "TEXT NOT NULL DEFAULT '0.00'"), ("MotivoDiferencia", "TEXT NOT NULL DEFAULT ''")
                })
                    if (!columnasTurno.Any(columna => columna.Name == nombre)) db.Execute($"ALTER TABLE TurnoCaja ADD COLUMN \"{nombre}\" {definicion}");
                db.CreateTable<NotaDebito>();
                db.CreateTable<PeriodoFiscal>();
                var columnasPeriodo = db.GetTableInfo("PeriodoFiscal");
                if (!columnasPeriodo.Any(columna => columna.Name == "UsuarioId")) db.Execute("ALTER TABLE PeriodoFiscal ADD COLUMN UsuarioId TEXT NOT NULL DEFAULT ''");
                db.CreateTable<DevolucionInventario>();
                db.CreateTable<HistorialCambioProducto>();
                db.CreateTable<BitacoraAuditoria>();
                db.CreateTable<HistorialExportacionDGII>();
                db.CreateTable<ConfiguracionAlerta>();
                var columnasAlerta = db.GetTableInfo("ConfiguracionAlerta");
                if (!columnasAlerta.Any(columna => columna.Name == "UsuarioId")) db.Execute("ALTER TABLE ConfiguracionAlerta ADD COLUMN UsuarioId TEXT NOT NULL DEFAULT ''");
                if (!columnasAlerta.Any(columna => columna.Name == "FechaAviso")) db.Execute("ALTER TABLE ConfiguracionAlerta ADD COLUMN FechaAviso TEXT NOT NULL DEFAULT ''");
                if (!columnasAlerta.Any(columna => columna.Name == "HoraAviso")) db.Execute("ALTER TABLE ConfiguracionAlerta ADD COLUMN HoraAviso TEXT NOT NULL DEFAULT '09:00'");
                db.CreateTable<PedidoCompra>();
                db.CreateTable<DetallePedidoCompra>();
                db.CreateTable<RegistroFiscal>();
                var columnasRegistroFiscal = db.GetTableInfo("RegistroFiscal");
                if (!columnasRegistroFiscal.Any(columna => columna.Name == "RazonSocialContraparte"))
                    db.Execute("ALTER TABLE RegistroFiscal ADD COLUMN RazonSocialContraparte TEXT NOT NULL DEFAULT ''");
                db.Execute("CREATE INDEX IF NOT EXISTS IX_Factura_NCF ON Factura(NCF)");
                db.Execute("CREATE INDEX IF NOT EXISTS IX_Factura_Fecha_Turno ON Factura(Fecha, TurnoId)");
                db.Execute("CREATE INDEX IF NOT EXISTS IX_Factura_RNC ON Factura(ClienteRnc)");
                db.Execute("CREATE INDEX IF NOT EXISTS IX_RegistroFiscal_NCF ON RegistroFiscal(Ncf)");
                db.Execute("CREATE INDEX IF NOT EXISTS IX_RegistroFiscal_RNC_Fecha ON RegistroFiscal(RncContraparte, FechaComprobante)");
                db.Execute("CREATE INDEX IF NOT EXISTS IX_RegistroFiscal_Turno ON RegistroFiscal(TurnoId)");
                db.Execute("CREATE INDEX IF NOT EXISTS IX_PeriodoFiscal_AnioMes ON PeriodoFiscal(Anio, Mes)");
                db.Execute("CREATE INDEX IF NOT EXISTS IX_HistorialCambioProducto_IdProducto_Fecha ON HistorialCambioProducto(IdProducto, FechaHora)");
                db.Execute("CREATE INDEX IF NOT EXISTS IX_BitacoraAuditoria_Usuario_Fecha ON BitacoraAuditoria(UsuarioId, FechaHora)");
                foreach (var facturaExistente in db.Query<Factura>("SELECT * FROM Factura WHERE NCF <> '' AND (Estado = ? OR Estado = ?)", "Emitida", "Anulada"))
                {
                    var facturaId = facturaExistente.Id.ToString("N");
                    if (!db.Table<RegistroFiscal>().Any(registro => registro.FacturaId == facturaId && registro.Ncf == facturaExistente.NCF))
                        db.Insert(CrearRegistroFiscalExistente(facturaExistente));
                }
                foreach (var notaExistente in db.Query<NotaCredito>("SELECT * FROM NotaCredito WHERE NCF <> '' AND Anulada = 0"))
                {
                    if (!db.Table<RegistroFiscal>().Any(registro => registro.TipoOperacion == "Venta" && registro.Ncf == notaExistente.NCF))
                    {
                        var facturaRelacionada = notaExistente.FacturaRelacionadaId is Guid facturaId ? db.Find<Factura>(facturaId) : null;
                        db.Insert(CrearRegistroFiscalNotaCredito(notaExistente, facturaRelacionada));
                    }
                }
                var secuenciaAnterior = db.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='SecuenciaNCF'") != 0;
                if (secuenciaAnterior && !db.GetTableInfo("SecuenciaNCF").Any(columna => columna.Name == "Tipo"))
                {
                    var ultimoNumero = db.ExecuteScalar<int>("SELECT COALESCE(MAX(UltimoNumero), 0) FROM SecuenciaNCF");
                    var existeLegado = db.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='SecuenciaNCF_Legado'") != 0;
                    if (existeLegado) throw new InvalidOperationException("Existe una secuencia NCF antigua y otra de respaldo; revisa la migración manualmente.");
                    db.Execute("ALTER TABLE SecuenciaNCF RENAME TO SecuenciaNCF_Legado");
                    db.CreateTable<SecuenciaNCF>();
                    db.Insert(new SecuenciaNCF { Tipo = "B01", UltimoNumero = ultimoNumero });
                }
                db.CreateTable<SecuenciaNCF>();
                foreach (var tipo in new[] { "B01", "B02", "B03", "B04", "B14", "B15", "B16", "B04-NC" })
                    if (db.Find<SecuenciaNCF>(tipo) == null)
                        db.Insert(new SecuenciaNCF { Tipo = tipo, UltimoNumero = 0 });
            });
            _documentosInitialized = true;
        }
        finally
        {
            _documentosLock.Release();
        }
    }

    private static void PrepararTablaDocumento<T>(SQLiteConnection db, string tabla, string legado, string[] columnasNecesarias) where T : new()
    {
        var existe = db.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=?", tabla) != 0;
        if (existe)
        {
            var columnas = db.GetTableInfo(tabla);
            var tipoId = db.ExecuteScalar<string>($"SELECT type FROM pragma_table_info('{tabla}') WHERE name='Id'");
            var completas = columnasNecesarias.All(nombre => columnas.Any(columna => columna.Name == nombre)) &&
                tipoId != null && tipoId.StartsWith("varchar", StringComparison.OrdinalIgnoreCase);
            if (!completas)
            {
                var existeLegado = db.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=?", legado) != 0;
                if (existeLegado)
                    throw new InvalidOperationException($"Existen las tablas {tabla} y {legado}; se requiere revisar manualmente la migración para conservar los datos.");
                db.Execute($"ALTER TABLE \"{tabla}\" RENAME TO \"{legado}\"");
            }
        }
        db.CreateTable<T>();
    }

    private static ConfiguracionUsuario ObtenerConfiguracion(SQLiteConnection db, string usuarioId)
    {
        var config = db.Find<ConfiguracionUsuario>(usuarioId);
        var ahora = DateTime.Now;
        var mes = new DateTime(ahora.Year, ahora.Month, 1, 0, 0, 0, DateTimeKind.Local);
        if (config == null)
        {
            config = new ConfiguracionUsuario { UsuarioId = usuarioId, UltimoMesConteo = mes };
            db.Insert(config);
        }
        else
        {
            var cambio = false;
            if (config.UltimoAnioConteo.Year != ahora.Year)
            {
                config.FacturasEsteAnio = 0;
                config.UltimoAnioConteo = new DateTime(ahora.Year, 1, 1, 0, 0, 0, DateTimeKind.Local);
                cambio = true;
            }
            if (config.LicenciaTipo == "Gratuita")
            {
                config.LicenciaTipo = "Gratuito";
                cambio = true;
            }
            if (config.UltimoMesConteo.Year != mes.Year || config.UltimoMesConteo.Month != mes.Month)
            {
                config.CotizacionesEsteMes = 0;
                config.FacturasEsteMes = 0;
                config.UltimoMesConteo = mes;
                cambio = true;
            }
            if (cambio) db.Update(config);
        }
        return config;
    }

    private static bool LicenciaActiva(ConfiguracionUsuario config) =>
        config.LicenciaTipo != "Gratuito" && config.LicenciaVencimiento is DateTime vencimiento && vencimiento > DateTime.UtcNow;

    private ConfiguracionUsuario ReservarDocumento(SQLiteConnection db, string usuarioId, bool cotizacion)
    {
        var config = ObtenerConfiguracion(db, usuarioId);
        if (cotizacion)
        {
            if (!LicenciaActiva(config) && !LicenciaRemotaActiva(usuarioId) && config.CotizacionesEsteMes >= 5)
                throw new LicenciaLimiteException(true, config.CotizacionesEsteMes);
            config.CotizacionesEsteMes++;
        }
        else
        {
            if (!LicenciaActiva(config) && !LicenciaRemotaActiva(usuarioId))
            {
                var ahora = DateTime.UtcNow;
                var facturas = db.Table<Factura>().Where(f => f.UsuarioId == usuarioId && f.TipoOperacion == "Venta" && f.NCF != string.Empty && (f.Estado == "Emitida" || f.Estado == "Anulada")).ToList();
                var ultimosTreintaDias = facturas.Count(f => f.Fecha >= ahora.AddDays(-30));
                var esteAnio = facturas.Count(f => f.Fecha >= new DateTime(ahora.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                if (ultimosTreintaDias >= 5 || esteAnio >= 40)
                    throw new LicenciaLimiteException(false, ultimosTreintaDias, esteAnio);
            }
            config.FacturasEsteMes++;
            config.FacturasEsteAnio++;
        }
        db.Update(config);
        return config;
    }

    private static readonly (string Tipo, string Descripcion)[] TiposNcf =
    {
        ("B01", "Factura de Crédito Fiscal"), ("B02", "Factura de Consumo Final"),
        ("B03", "Nota de Débito"), ("B04", "Nota de Crédito"),
        ("B14", "Comprobante Régimen Especial"), ("B15", "Comprobante de Pago"),
        ("B16", "Factura Gubernamental"), ("B13", "Gastos menores (solo autorización DGII)"),
        ("A02", "Serie heredada A02 (solo autorización DGII)"), ("A03", "Serie heredada A03 (solo autorización DGII)")
    };

    private static string IdNcf(string usuarioId, string tipo) => $"{usuarioId}|{tipo}";

    public async Task<List<NcfUsuario>> ObtenerConfiguracionNcfAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db =>
        {
            var configuraciones = db.Table<NcfUsuario>().Where(n => n.UsuarioId == usuarioId).ToList();
            foreach (var (tipo, descripcion) in TiposNcf)
            {
                if (configuraciones.Any(n => n.Tipo == tipo)) continue;
                var anterior = db.Find<SecuenciaNCF>(tipo)?.UltimoNumero ?? 0;
                var activa = false;
                var config = new NcfUsuario
                {
                    Id = IdNcf(usuarioId, tipo), UsuarioId = usuarioId, Tipo = tipo,
                    Descripcion = descripcion, Activo = activa, Prefijo = tipo,
                    SiguienteNumero = Math.Max(1, anterior + 1), NumeroFinal = 0
                };
                db.Insert(config);
                configuraciones.Add(config);
            }
            return TiposNcf.Select(tipo => configuraciones.Single(config => config.Tipo == tipo.Tipo)).ToList();
        });
    }

    public async Task GuardarConfiguracionNcfAsync(string usuarioId, IReadOnlyCollection<NcfUsuario> configuraciones)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        ArgumentNullException.ThrowIfNull(configuraciones);
        if (configuraciones.Count != TiposNcf.Length || TiposNcf.Any(tipo => configuraciones.Count(config => config.Tipo == tipo.Tipo) != 1))
            throw new InvalidOperationException("La configuración debe incluir una sola fila para cada tipo NCF disponible.");
        foreach (var config in configuraciones)
        {
            if ((config.Prefijo != config.Tipo && config.Prefijo != config.Tipo + "-") || config.SiguienteNumero is < 1 or > 100_000_000 || config.NumeroFinal is < 0 or > 99_999_999 ||
                (config.Activo && config.NumeroFinal == 0))
                throw new InvalidOperationException($"Revisa la secuencia y el rango autorizado para {config.Tipo}.");
        }
        await InitTablasDocumentosAsync();
        await _db.RunInTransactionAsync(db =>
        {
            foreach (var config in configuraciones)
            {
                var id = IdNcf(usuarioId, config.Tipo);
                var anterior = db.Find<NcfUsuario>(id);
                if (anterior != null && config.SiguienteNumero < anterior.SiguienteNumero)
                    throw new InvalidOperationException($"No se puede retroceder la secuencia {config.Tipo}; ya se reservaron números hasta {anterior.SiguienteNumero - 1:D8}.");
                if (anterior != null && anterior.SiguienteNumero > 1 && config.Prefijo != anterior.Prefijo)
                    throw new InvalidOperationException($"No se puede cambiar el prefijo de {config.Tipo} después de emitir comprobantes.");
                config.Id = id;
                config.UsuarioId = usuarioId;
                config.Descripcion = TiposNcf.Single(tipo => tipo.Tipo == config.Tipo).Descripcion;
                db.InsertOrReplace(config);
            }
        });
    }

    private static string GenerarSiguienteNCF(SQLiteConnection db, string usuarioId, string tipo)
    {
        var configuracion = db.Find<NcfUsuario>(IdNcf(usuarioId, tipo));
        if (configuracion == null || !configuracion.Activo)
            throw new InvalidOperationException("Este tipo de comprobante no está registrado.");
        if (configuracion.SiguienteNumero > configuracion.NumeroFinal)
            throw new InvalidOperationException("Has llegado al final de la secuencia. Solicita una nueva a la DGII.");
        var numero = configuracion.SiguienteNumero;
        string ncf;
        do
        {
            if (numero > configuracion.NumeroFinal) throw new InvalidOperationException("La secuencia configurada no tiene números disponibles sin duplicar; solicita una nueva a la DGII.");
            ncf = $"{configuracion.Prefijo}{numero:D8}";
            numero++;
        } while (db.Table<Factura>().Any(item => item.UsuarioId == usuarioId && item.NCF == ncf) ||
                 db.Table<NotaCredito>().Any(item => item.UsuarioId == usuarioId && item.NCF == ncf) ||
                 db.Table<NotaDebito>().Any(item => item.IdUsuario == usuarioId && item.NCF == ncf));
        configuracion.SiguienteNumero = numero;
        db.Update(configuracion);
        var secuencia = db.Find<SecuenciaNCF>(tipo) ?? new SecuenciaNCF { Tipo = tipo };
        secuencia.UltimoNumero = Math.Max(secuencia.UltimoNumero, numero - 1);
        if (db.Find<SecuenciaNCF>(tipo) == null) db.Insert(secuencia);
        else db.Update(secuencia);
        return ncf;
    }

    private async Task<T> EnTransaccionAsync<T>(Func<SQLiteConnection, T> operacion)
    {
        T resultado = default!;
        await _db.RunInTransactionAsync(db => resultado = operacion(db));
        return resultado;
    }

    public async Task<ConfiguracionUsuario> ObtenerConfiguracionUsuarioAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        var config = await EnTransaccionAsync(db => ObtenerConfiguracion(db, usuarioId));
        var remota = Volatile.Read(ref _licenciaRemota);
        if (remota is not null && LicenciaRemotaActiva(remota, usuarioId) &&
            (remota.PriorizarRemota || !LicenciaActiva(config)))
        {
            config.LicenciaTipo = "Remota";
            config.LicenciaVencimiento = remota.VencimientoUtc;
            config.FechaInicioPruebaUtc = null;
            config.FechaVencimientoPruebaUtc = null;
        }
        return config;
    }

    public async Task<ConfiguracionUsuario> ObtenerConfiguracionLicenciaSinActualizarAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await _db.FindAsync<ConfiguracionUsuario>(usuarioId)
            ?? new ConfiguracionUsuario { UsuarioId = usuarioId };
    }

    public async Task GuardarConfiguracionUsuarioAsync(ConfiguracionUsuario configuracion)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        await InitTablasDocumentosAsync();
        await _db.RunInTransactionAsync(db =>
        {
            var actual = ObtenerConfiguracion(db, configuracion.UsuarioId);
            actual.CotizacionesEsteMes = configuracion.CotizacionesEsteMes;
            actual.FacturasEsteMes = configuracion.FacturasEsteMes;
            db.Update(actual);
        });
    }

    public async Task IncrementarContadorDocumentoAsync(string usuarioId, bool cotizacion)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        await _db.RunInTransactionAsync(db => ReservarDocumento(db, usuarioId, cotizacion));
    }

    public async Task ActivarPruebaGratuitaAsync(string usuarioId, string identificadorHardware, DateTime inicioUtc, DateTime vencimientoUtc)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        ArgumentException.ThrowIfNullOrWhiteSpace(identificadorHardware);
        inicioUtc = DateTime.SpecifyKind(inicioUtc, DateTimeKind.Utc);
        vencimientoUtc = DateTime.SpecifyKind(vencimientoUtc, DateTimeKind.Utc);
        if (vencimientoUtc != inicioUtc.AddDays(LicenciaPruebaService.DuracionPruebaDias))
            throw new ArgumentException("La prueba debe vencer exactamente 30 días después de su activación.", nameof(vencimientoUtc));
        await InitTablasDocumentosAsync();
        await _db.RunInTransactionAsync(db =>
        {
            var config = ObtenerConfiguracion(db, usuarioId);
            var esMismaPrueba = config.LicenciaTipo == LicenciaPruebaService.TipoPrueba &&
                config.FechaInicioPruebaUtc == inicioUtc && config.FechaVencimientoPruebaUtc == vencimientoUtc &&
                string.Equals(config.IdentificadorHardware, identificadorHardware, StringComparison.Ordinal);
            if (!esMismaPrueba && (config.LicenciaTipo != "Gratuito" || config.FechaInicioPruebaUtc.HasValue || !string.IsNullOrWhiteSpace(config.IdentificadorHardware)))
                throw new InvalidOperationException("Ya existe una licencia o prueba registrada para esta cuenta.");

            config.LicenciaTipo = LicenciaPruebaService.TipoPrueba;
            config.LicenciaVencimiento = vencimientoUtc;
            config.FechaInicioPruebaUtc = inicioUtc;
            config.FechaVencimientoPruebaUtc = vencimientoUtc;
            config.IdentificadorHardware = identificadorHardware;
            db.Update(config);
        });
    }

#if DEBUG
    public async Task ReiniciarPruebaDebugAsync(string usuarioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usuarioId);
        await InitTablasDocumentosAsync();
        await _db.RunInTransactionAsync(db =>
        {
            var config = db.Find<ConfiguracionUsuario>(usuarioId);
            if (config is null) return;
            if (config.LicenciaTipo is not ("Gratuito" or "Gratuita") && config.LicenciaTipo != LicenciaPruebaService.TipoPrueba)
                throw new InvalidOperationException("No se puede reiniciar la prueba porque la cuenta tiene una licencia pagada registrada.");
            config.LicenciaTipo = "Gratuito";
            config.LicenciaVencimiento = null;
            config.FechaInicioPruebaUtc = null;
            config.FechaVencimientoPruebaUtc = null;
            config.IdentificadorHardware = string.Empty;
            db.Update(config);
        });
    }
#endif

    public async Task ActivarLicenciaAsync(string usuarioId, string tipo)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        if (tipo is not ("Mensual" or "Trimestral" or "Semestral" or "Anual"))
            throw new ArgumentOutOfRangeException(nameof(tipo), "El plan debe ser Mensual, Trimestral, Semestral o Anual.");
        await InitTablasDocumentosAsync();
        await _db.RunInTransactionAsync(db =>
        {
            var config = ObtenerConfiguracion(db, usuarioId);
            var ahora = DateTime.UtcNow;
            var inicio = LicenciaActiva(config) && config.LicenciaVencimiento > ahora ? config.LicenciaVencimiento.Value : ahora;
            config.LicenciaTipo = tipo;
            var dias = tipo switch { "Mensual" => 30, "Trimestral" => 90, "Semestral" => 180, _ => 365 };
            config.LicenciaVencimiento = inicio.AddDays(dias);
            db.Update(config);
        });
    }

    public async Task<List<Cotizacion>> GetCotizacionesAsync(string usuarioId)
    {
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<Cotizacion>("SELECT * FROM Cotizacion WHERE UsuarioId = ? ORDER BY Fecha DESC, ClienteNombre COLLATE NOCASE", usuarioId);
    }

    public async Task<Cotizacion?> GetCotizacionByIdAsync(Guid id, string usuarioId)
    {
        await InitTablasDocumentosAsync();
        return await _db.FindWithQueryAsync<Cotizacion>("SELECT * FROM Cotizacion WHERE Id = ? AND UsuarioId = ?", id, usuarioId);
    }

    public async Task GuardarCotizacionAsync(Cotizacion cotizacion, string usuarioId)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new InvalidOperationException("No se pudo identificar al usuario autenticado.");
        await InitTablasDocumentosAsync();
        await _db.RunInTransactionAsync(db =>
        {
            if (cotizacion.Id == Guid.Empty)
            {
                ReservarDocumento(db, usuarioId, true);
                cotizacion.Id = Guid.NewGuid();
                cotizacion.UsuarioId = usuarioId;
                cotizacion.Fecha = DateTime.UtcNow;
                cotizacion.ItemsJson = System.Text.Json.JsonSerializer.Serialize(cotizacion.Items);
                db.Insert(cotizacion);
            }
            else
            {
                var existente = db.Find<Cotizacion>(cotizacion.Id) ?? throw new InvalidOperationException("La cotización ya no existe.");
                if (existente.UsuarioId != usuarioId) throw new InvalidOperationException("No tienes permiso para modificar esta cotización.");
                cotizacion.UsuarioId = usuarioId;
                if (existente.ConvertidaAFactura) throw new InvalidOperationException("No se puede editar una cotización convertida en factura.");
                cotizacion.Fecha = existente.Fecha;
                cotizacion.FechaConversion = existente.FechaConversion;
                cotizacion.ConvertidaAFactura = existente.ConvertidaAFactura;
                cotizacion.ItemsJson = System.Text.Json.JsonSerializer.Serialize(cotizacion.Items);
                db.Update(cotizacion);
            }
        });
    }

    public async Task<int> EliminarCotizacionAsync(Guid id, string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db =>
        {
            var cotizacion = db.Find<Cotizacion>(id);
            if (cotizacion?.UsuarioId != usuarioId) throw new InvalidOperationException("No tienes permiso para eliminar esta cotización.");
            if (cotizacion.ConvertidaAFactura) throw new InvalidOperationException("No se puede eliminar una cotización convertida en factura.");
            return db.Delete<Cotizacion>(id);
        });
    }

    public async Task<Factura> ConvertirCotizacionAFacturaAsync(Guid cotizacionId, string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new InvalidOperationException("No se pudo identificar al usuario autenticado.");
        await InitTablasDocumentosAsync();
        await ObtenerConfiguracionNcfAsync(usuarioId);
        return await EnTransaccionAsync(db =>
        {
            var cotizacion = db.Table<Cotizacion>().FirstOrDefault(c => c.Id == cotizacionId && c.UsuarioId == usuarioId)
                ?? throw new InvalidOperationException("No se encontró la cotización.");
            if (cotizacion.ConvertidaAFactura) throw new InvalidOperationException("Esta cotización ya fue convertida.");
            ReservarDocumento(db, usuarioId, false);
            var cliente = db.Find<Cliente>(cotizacion.ClienteId);
            var ajustes = db.Find<AjustesUsuario>(usuarioId);
            var recargoPorcentaje = ajustes?.RecargoActivo == true ? ajustes.RecargoPorcentaje : 0m;
            var recargo = decimal.Round(cotizacion.Total * recargoPorcentaje / 100m, 2);
            var metodosPago = new List<string>();
            if (ajustes?.PagoEfectivo == true) metodosPago.Add("Efectivo");
            if (ajustes?.PagoTransferencia == true) metodosPago.Add("Transferencia");
            if (ajustes?.PagoCheque == true) metodosPago.Add("Cheque");
            if (ajustes?.PagoTarjeta == true) metodosPago.Add("Tarjeta");
            if (ajustes?.PagoCredito == true) metodosPago.Add("Crédito");
            if (ajustes?.PagoOtro == true && !string.IsNullOrWhiteSpace(ajustes.MetodoOtro)) metodosPago.Add(ajustes.MetodoOtro);
            var instruccionesPago = metodosPago.Count > 0 ? $"Métodos de pago: {string.Join(", ", metodosPago)}" : string.Empty;
            if (ajustes?.PagoTransferencia == true && !string.IsNullOrWhiteSpace(ajustes.BancoNombre))
                instruccionesPago += $"\nBanco: {ajustes.BancoNombre}; cuenta {ajustes.BancoTipoCuenta}: {ajustes.BancoNumeroCuenta}; RNC: {ajustes.BancoRnc}";
            if (!string.IsNullOrWhiteSpace(ajustes?.MensajeReferencia)) instruccionesPago += $"\n{ajustes.MensajeReferencia}";
            if (!string.IsNullOrWhiteSpace(ajustes?.InstruccionPago)) instruccionesPago += $"\n{ajustes.InstruccionPago}";
            var fechaEmision = DateTime.UtcNow;
            var configuracionesNcf = db.Table<NcfUsuario>().Where(n => n.UsuarioId == usuarioId && n.Activo && (n.Tipo == "B01" || n.Tipo == "B02")).OrderBy(n => n.Tipo).ToList();
            var configuracionNcf = configuracionesNcf.FirstOrDefault() ?? throw new InvalidOperationException("Este tipo de comprobante no está registrado.");
            var factura = new Factura
            {
                Id = Guid.NewGuid(), TipoNCF = configuracionNcf.Tipo, NCF = GenerarSiguienteNCF(db, usuarioId, configuracionNcf.Tipo), ClienteId = cotizacion.ClienteId,
                ClienteNombre = cotizacion.ClienteNombre, ClienteRnc = cliente?.RncCedula ?? string.Empty, Fecha = fechaEmision,
                ItemsJson = cotizacion.ItemsJson, Subtotal = cotizacion.Subtotal, Itbis = cotizacion.Itbis,
                Total = cotizacion.Total + recargo, RecargoPorcentaje = recargoPorcentaje, RecargoMonto = recargo,
                InstruccionPago = instruccionesPago, VenceFecha = ajustes?.VenceEnDias > 0 ? fechaEmision.AddDays(ajustes.VenceEnDias) : null, UsuarioId = usuarioId
            };
            var turno = ObtenerOAbrirTurnoActual(db, usuarioId, _now());
            factura.TurnoId = turno.Id;
            DescontarInventarioVenta(db, factura.Items);
            db.Insert(CrearRegistroFiscalVenta(db, factura));
            db.Insert(factura);
            cotizacion.ConvertidaAFactura = true;
            cotizacion.FechaConversion = factura.Fecha;
            db.Update(cotizacion);
            return factura;
        });
    }

    public async Task<Cotizacion> ConvertirFacturaACotizacionAsync(Guid facturaId, string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db =>
        {
            var factura = db.Table<Factura>().FirstOrDefault(f => f.Id == facturaId && f.UsuarioId == usuarioId)
                ?? throw new InvalidOperationException("No se encontró la factura.");
            if (factura.Estado != "Emitida" || factura.Anulada || string.IsNullOrWhiteSpace(factura.NCF))
                throw new InvalidOperationException("Solo se pueden convertir facturas emitidas y vigentes.");
            ReservarDocumento(db, usuarioId, true);
            var cotizacion = new Cotizacion
            {
                Id = Guid.NewGuid(), ClienteId = factura.ClienteId, ClienteNombre = factura.ClienteNombre, UsuarioId = usuarioId,
                Fecha = DateTime.UtcNow, ValidezHasta = DateTime.Today.AddDays(15), ItemsJson = factura.ItemsJson,
                Subtotal = factura.Subtotal, Itbis = factura.Itbis, Total = factura.Subtotal + factura.Itbis
            };
            db.Insert(cotizacion);
            return cotizacion;
        });
    }

    public async Task GuardarFacturaAsync(Factura factura, bool emitir = true)
    {
        ArgumentNullException.ThrowIfNull(factura);
        await InitTablasDocumentosAsync();
        if (emitir) await ObtenerConfiguracionNcfAsync(factura.UsuarioId);
        await _db.RunInTransactionAsync(db =>
        {
            if (string.IsNullOrWhiteSpace(factura.UsuarioId)) throw new InvalidOperationException("No se pudo identificar al usuario autenticado.");
            factura.ItemsJson = System.Text.Json.JsonSerializer.Serialize(factura.Items);
            var esNueva = factura.Id == Guid.Empty;
            if (esNueva)
            {
                factura.Id = Guid.NewGuid();
                factura.Fecha = DateTime.UtcNow;
            }
            else
            {
                var existente = db.Find<Factura>(factura.Id) ?? throw new InvalidOperationException("La factura ya no existe.");
                if (existente.UsuarioId != factura.UsuarioId) throw new InvalidOperationException("No tienes permiso para modificar esta factura.");
                if (existente.Estado != "Guardada" || existente.Anulada) throw new InvalidOperationException("Solo se pueden editar facturas guardadas que aún no se han emitido.");
                factura.Fecha = existente.Fecha;
                factura.NCF = existente.NCF;
            }
            var seEmiteVentaNueva = emitir && factura.TipoOperacion == "Venta" && (esNueva || !string.Equals(db.Find<Factura>(factura.Id)?.Estado, "Emitida", StringComparison.Ordinal));
            if (emitir)
            {
                if (string.IsNullOrWhiteSpace(factura.NCF))
                {
                    factura.Fecha = DateTime.UtcNow;
                    ReservarDocumento(db, factura.UsuarioId, false);
                    factura.NCF = GenerarSiguienteNCF(db, factura.UsuarioId, factura.TipoNCF);
                }
                factura.Estado = "Emitida";
                if (factura.VencimientoEliminado) factura.VenceFecha = null;
                else if (factura.VenceFecha == null)
                {
                    var ajustes = db.Find<AjustesUsuario>(factura.UsuarioId);
                    if (ajustes?.VenceEnDias > 0) factura.VenceFecha = factura.Fecha.AddDays(ajustes.VenceEnDias);
                }
            }
            else
            {
                factura.NCF = string.Empty;
                factura.Estado = "Guardada";
            }
            if (factura.NCF.Length != 0 && !factura.NCF.StartsWith(factura.TipoNCF, StringComparison.Ordinal))
                throw new InvalidOperationException("El tipo de comprobante no coincide con el NCF.");
            if (seEmiteVentaNueva)
            {
                var turno = ObtenerOAbrirTurnoActual(db, factura.UsuarioId, _now());
                factura.TurnoId = turno.Id;
                DescontarInventarioVenta(db, factura.Items);
                db.Insert(CrearRegistroFiscalVenta(db, factura));
            }
            AsegurarPeriodoAbierto(db, factura.UsuarioId, factura.Fecha);
            if (esNueva) db.Insert(factura);
            else if (db.Update(factura) == 0) throw new InvalidOperationException("No se pudo actualizar la factura.");
        });
    }

    public async Task<List<Factura>> GetFacturasAsync(string usuarioId)
    {
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<Factura>("SELECT * FROM Factura WHERE UsuarioId = ? AND TipoOperacion = 'Venta' ORDER BY Fecha DESC, NCF DESC", usuarioId);
    }

    public async Task<List<Factura>> GetFacturasPorOperacionAsync(string usuarioId, string tipoOperacion)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        if (tipoOperacion is not ("Venta" or "Compra")) throw new ArgumentException("El tipo de operación debe ser Venta o Compra.", nameof(tipoOperacion));
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<Factura>("SELECT * FROM Factura WHERE UsuarioId = ? AND TipoOperacion = ? ORDER BY Fecha, NCF", usuarioId, tipoOperacion);
    }

    public async Task<bool> GuardarFacturaImportadaAsync(Factura factura)
    {
        ArgumentNullException.ThrowIfNull(factura);
        if (string.IsNullOrWhiteSpace(factura.UsuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(factura));
        if (factura.TipoOperacion is not ("Venta" or "Compra")) throw new ArgumentException("El tipo de operación debe ser Venta o Compra.", nameof(factura));
        if (string.IsNullOrWhiteSpace(factura.NCF)) throw new ArgumentException("El NCF es obligatorio.", nameof(factura));
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db =>
        {
            AsegurarPeriodoAbierto(db, factura.UsuarioId, factura.Fecha == default ? DateTime.UtcNow : factura.Fecha);
            var duplicada = db.Table<Factura>().Any(f => f.UsuarioId == factura.UsuarioId && f.TipoOperacion == factura.TipoOperacion && f.NCF == factura.NCF && (factura.TipoOperacion == "Venta" || f.ClienteRnc == factura.ClienteRnc));
            if (duplicada) return false;
            factura.Id = Guid.NewGuid();
            factura.Fecha = factura.Fecha == default ? DateTime.UtcNow : factura.Fecha.ToUniversalTime();
            factura.Estado = "Emitida";
            factura.Anulada = false;
            factura.ItemsJson = System.Text.Json.JsonSerializer.Serialize(factura.Items);
            db.Insert(factura);
            db.Insert(CrearRegistroFiscalImportado(factura));
            return true;
        });
    }

    public async Task<Factura?> GetFacturaByIdAsync(Guid id, string usuarioId)
    {
        await InitTablasDocumentosAsync();
        return await _db.FindWithQueryAsync<Factura>("SELECT * FROM Factura WHERE Id = ? AND UsuarioId = ?", id, usuarioId);
    }

    public async Task MarcarFacturaEnviadaAsync(Guid id, string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        await EnTransaccionAsync(db =>
        {
            var factura = db.Table<Factura>().FirstOrDefault(f => f.Id == id && f.UsuarioId == usuarioId)
                ?? throw new InvalidOperationException("No se encontró la factura.");
            if (factura.Estado != "Emitida" || factura.Anulada) throw new InvalidOperationException("Solo se pueden enviar facturas emitidas y vigentes.");
            factura.Enviada = true;
            db.Update(factura);
            return 0;
        });
    }

    public async Task MarcarCotizacionEnviadaAsync(Guid id, string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        await EnTransaccionAsync(db =>
        {
            var cotizacion = db.Table<Cotizacion>().FirstOrDefault(c => c.Id == id && c.UsuarioId == usuarioId)
                ?? throw new InvalidOperationException("No se encontró la cotización.");
            cotizacion.Enviada = true;
            db.Update(cotizacion);
            return 0;
        });
    }

    public async Task<List<NotaCredito>> GetAllNotasCreditoAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<NotaCredito>("SELECT * FROM NotaCredito WHERE UsuarioId = ? ORDER BY Fecha DESC, NCF DESC", usuarioId);
    }

    public async Task<List<NotaCredito>> GetNotasCreditoPorClienteAsync(Guid clienteId, string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<NotaCredito>("SELECT * FROM NotaCredito WHERE ClienteId = ? AND UsuarioId = ? ORDER BY Fecha DESC", clienteId, usuarioId);
    }

    public async Task<List<Factura>> GetFacturasPendientesAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<Factura>("SELECT * FROM Factura WHERE UsuarioId = ? AND TipoOperacion = 'Venta' AND Estado = 'Emitida' AND EstadoPago = 'Pendiente' AND Anulada = 0 ORDER BY VenceFecha, Fecha", usuarioId);
    }

    public async Task<(int Ultimos30Dias, int EsteAnio)> ObtenerConsumoLiteFacturasAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        var ahora = DateTime.UtcNow;
        var facturas = await _db.QueryAsync<Factura>("SELECT * FROM Factura WHERE UsuarioId = ? AND TipoOperacion = 'Venta' AND NCF != '' AND (Estado = 'Emitida' OR Estado = 'Anulada')", usuarioId);
        return (facturas.Count(f => f.Fecha >= ahora.AddDays(-30)), facturas.Count(f => f.Fecha >= new DateTime(ahora.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    public async Task<bool> PuedeCrearFacturaAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        var estado = await ObtenerConfiguracionUsuarioAsync(usuarioId);
        if (LicenciaActiva(estado)) return true;
        var consumo = await ObtenerConsumoLiteFacturasAsync(usuarioId);
        return consumo.Ultimos30Dias < 5 && consumo.EsteAnio < 40;
    }

    public async Task<List<Factura>> GetFacturasPendientesAsync(Guid clienteId, string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        var facturas = await _db.QueryAsync<Factura>("SELECT * FROM Factura WHERE ClienteId = ? AND UsuarioId = ? AND TipoOperacion = 'Venta' AND Estado = 'Emitida' AND EstadoPago = 'Pendiente' AND Anulada = 0 ORDER BY Fecha DESC", clienteId, usuarioId);
        var notas = await _db.QueryAsync<NotaCredito>("SELECT * FROM NotaCredito WHERE ClienteId = ? AND UsuarioId = ? AND Anulada = 0 AND NCF != '' AND FacturaRelacionadaId IS NOT NULL", clienteId, usuarioId);
        return facturas.Where(f => f.Total - notas.Where(n => n.FacturaRelacionadaId == f.Id).Sum(n => n.Total) > 0).ToList();
    }

    public async Task GuardarBorradorNotaCreditoAsync(NotaCredito nota)
    {
        ArgumentNullException.ThrowIfNull(nota);
        if (string.IsNullOrWhiteSpace(nota.UsuarioId)) throw new InvalidOperationException("Inicia sesión para guardar el borrador.");
        await InitTablasDocumentosAsync();
        await _db.RunInTransactionAsync(db =>
        {
            if (nota.Id == Guid.Empty)
            {
                nota.Id = Guid.NewGuid();
                nota.Fecha = DateTime.UtcNow;
                nota.FechaCreacion = nota.Fecha;
                nota.NCF = string.Empty;
                db.Insert(nota);
            }
            else
            {
                var anterior = db.Find<NotaCredito>(nota.Id) ?? throw new InvalidOperationException("La nota ya no existe.");
                if (anterior.UsuarioId != nota.UsuarioId || !anterior.EsBorrador || anterior.Anulada)
                    throw new InvalidOperationException("Solo se pueden modificar borradores propios de notas de crédito.");
                nota.Fecha = anterior.Fecha;
                nota.FechaCreacion = anterior.FechaCreacion;
                nota.NCF = string.Empty;
                if (db.Update(nota) == 0) throw new InvalidOperationException("No se pudo actualizar el borrador.");
            }
        });
    }

    public async Task GuardarNotaCreditoAsync(NotaCredito notaCredito)
    {
        ArgumentNullException.ThrowIfNull(notaCredito);
        if (string.IsNullOrWhiteSpace(notaCredito.UsuarioId)) throw new InvalidOperationException("No se pudo identificar al usuario autenticado.");
        await ObtenerConfiguracionNcfAsync(notaCredito.UsuarioId);
        if (notaCredito.ClienteId == Guid.Empty || string.IsNullOrWhiteSpace(notaCredito.Motivo)) throw new InvalidOperationException("Selecciona un cliente y un motivo.");
        if (notaCredito.Items.Count == 0 || notaCredito.Items.Any(item => string.IsNullOrWhiteSpace(item.Descripcion) || item.Cantidad <= 0 || item.PrecioUnitario < 0))
            throw new InvalidOperationException("Agrega al menos una línea válida al detalle.");
        if (notaCredito.Total <= 0) throw new InvalidOperationException("El total de la nota de crédito debe ser mayor que cero.");
        await InitTablasDocumentosAsync();
        await _db.RunInTransactionAsync(db =>
        {
            var esNueva = notaCredito.Id == Guid.Empty;
            var anterior = esNueva ? null : db.Find<NotaCredito>(notaCredito.Id) ?? throw new InvalidOperationException("El borrador ya no existe.");
            if (anterior is not null && (anterior.UsuarioId != notaCredito.UsuarioId || !anterior.EsBorrador || anterior.Anulada))
                throw new InvalidOperationException("Las notas de crédito emitidas no se pueden editar.");
            var cliente = db.Find<Cliente>(notaCredito.ClienteId) ?? throw new InvalidOperationException("El cliente seleccionado ya no existe.");
            notaCredito.ClienteNombre = cliente.Nombre;
            notaCredito.ClienteRnc = cliente.RncCedula;
            if (notaCredito.FacturaRelacionadaId is Guid facturaId)
            {
                var factura = db.Table<Factura>().FirstOrDefault(f => f.Id == facturaId && f.UsuarioId == notaCredito.UsuarioId && f.ClienteId == cliente.Id && f.Estado == "Emitida" && !f.Anulada)
                    ?? throw new InvalidOperationException("La factura relacionada no está disponible para este cliente.");
                var yaAplicado = db.Table<NotaCredito>().ToList().Where(n => n.FacturaRelacionadaId == facturaId && n.UsuarioId == notaCredito.UsuarioId && !n.Anulada && !n.EsBorrador).Sum(n => n.Total);
                if (notaCredito.Total > factura.Total - yaAplicado) throw new InvalidOperationException("El total de la nota supera el saldo estimado de la factura.");
            }
            if (esNueva) notaCredito.Id = Guid.NewGuid();
            notaCredito.Fecha = DateTime.UtcNow;
            AsegurarPeriodoAbierto(db, notaCredito.UsuarioId, notaCredito.Fecha);
            notaCredito.FechaCreacion = anterior?.FechaCreacion ?? notaCredito.Fecha;
            notaCredito.NCF = GenerarSiguienteNCF(db, notaCredito.UsuarioId, "B04");
            notaCredito.ItemsJson = System.Text.Json.JsonSerializer.Serialize(notaCredito.Items);
            notaCredito.Anulada = false;
            if (esNueva) db.Insert(notaCredito);
            else if (db.Update(notaCredito) == 0) throw new InvalidOperationException("No se pudo emitir el borrador.");
            if (!db.Table<RegistroFiscal>().Any(registro => registro.UsuarioId == notaCredito.UsuarioId && registro.Ncf == notaCredito.NCF))
            {
                var facturaRelacionadaId = notaCredito.FacturaRelacionadaId;
                var facturaRelacionada = facturaRelacionadaId is Guid idFacturaRelacionada ? db.Find<Factura>(idFacturaRelacionada) : null;
                db.Insert(CrearRegistroFiscalNotaCredito(notaCredito, facturaRelacionada));
            }
        });
    }

    public async Task<NotaDebito> EmitirNotaDebitoAsync(NotaDebito nota)
    {
        ArgumentNullException.ThrowIfNull(nota);
        if (string.IsNullOrWhiteSpace(nota.IdUsuario) || nota.IdFacturaOrigen == Guid.Empty || string.IsNullOrWhiteSpace(nota.Motivo))
            throw new InvalidOperationException("Inicia sesión, selecciona una factura y escribe el motivo de la nota de débito.");
        if (nota.MontoFacturado <= 0 || nota.Itbis < 0 || nota.TotalMonto != nota.MontoFacturado + nota.Itbis)
            throw new InvalidOperationException("Los importes de la nota de débito no son válidos o no cuadran.");
        await ObtenerConfiguracionNcfAsync(nota.IdUsuario);
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db =>
        {
            var factura = db.Table<Factura>().FirstOrDefault(item => item.Id == nota.IdFacturaOrigen && item.UsuarioId == nota.IdUsuario && item.TipoOperacion == "Venta" && item.Estado == "Emitida" && !item.Anulada)
                ?? throw new InvalidOperationException("La factura afectada no está disponible o no pertenece a este usuario.");
            AsegurarPeriodoAbierto(db, nota.IdUsuario, _now());
            AsegurarPeriodoAbierto(db, nota.IdUsuario, factura.Fecha);
            var configuracion = db.Find<NcfUsuario>(IdNcf(nota.IdUsuario, "B03"));
            if (configuracion is null || !configuracion.Activo) throw new InvalidOperationException("Configura y activa una secuencia B03 antes de emitir notas de débito.");
            nota.IdNota = nota.IdNota == Guid.Empty ? Guid.NewGuid() : nota.IdNota;
            nota.TipoNota = "D";
            nota.NCF = GenerarSiguienteNCF(db, nota.IdUsuario, "B03");
            nota.NCFAfectado = factura.NCF;
            nota.RncComprador = factura.ClienteRnc;
            nota.NombreComprador = factura.ClienteNombre;
            nota.IdTurno = factura.TurnoId;
            nota.FechaEmision = _now();
            nota.FechaRegistro = DateTime.UtcNow;
            nota.Estado = "A";
            db.Insert(nota);
            db.Insert(new RegistroFiscal
            {
                UsuarioId = nota.IdUsuario, TipoOperacion = "Venta", FacturaId = factura.Id.ToString("N"), TurnoId = factura.TurnoId,
                FechaComprobante = nota.FechaEmision, RncContraparte = nota.RncComprador, Ncf = nota.NCF, NcfModificado = nota.NCFAfectado,
                TipoBienServicio = "Pendiente de clasificación (nota de débito)", TipoIngresoGasto = "Nota de débito: " + nota.Motivo,
                MontoGravadoTotal = F(nota.MontoFacturado), MontoExento = "0.00", TotalFacturado = F(nota.TotalMonto),
                ItbisFacturado = F(nota.Itbis), ItbisRetenido = "0.00", IsrRetenido = "0.00", OtrosImpuestos = "0.00", PropinaLegal = "0.00",
                CondicionPago = factura.EstadoPago, MetodoPago = factura.MetodoPago, EstadoRevision = "Pendiente de revisión fiscal",
                FuenteJson = System.Text.Json.JsonSerializer.Serialize(nota)
            });
            return nota;
        });
    }

    public async Task<int> AnularNotaCreditoAsync(Guid id, string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        var nota = await _db.FindAsync<NotaCredito>(id);
        if (nota is null || nota.UsuarioId != usuarioId) throw new InvalidOperationException("No se encontró la nota de crédito.");
        if (nota.EsBorrador) throw new InvalidOperationException("Un borrador no se puede anular como nota emitida.");
        if (nota.Anulada) return 0;
        await _db.InsertAsync(new BitacoraAuditoria
        {
            FechaHora = _now(), UsuarioId = usuarioId, Accion = "Anulación de nota bloqueada", TipoEntidad = "NotaCredito", IdEntidad = id.ToString("N"),
            Detalle = "Intento rechazado: no hay autorización administrativa verificada."
        });
        throw new InvalidOperationException("La anulación de notas requiere autorización administrativa verificada. La autenticación actual no tiene roles de administrador configurados; la nota no fue modificada.");
    }

    public async Task<NotaCredito> AnularFacturaAsync(Guid id, string usuarioId, string motivo)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length > 500) throw new ArgumentException("Escribe un motivo de anulación de hasta 500 caracteres.", nameof(motivo));
        await InitTablasDocumentosAsync();
        await ObtenerConfiguracionNcfAsync(usuarioId);
        return await EnTransaccionAsync(db =>
        {
            var factura = db.Table<Factura>().FirstOrDefault(f => f.Id == id && f.UsuarioId == usuarioId)
                ?? throw new InvalidOperationException("No se encontró la factura.");
            if (factura.Anulada || factura.Estado == "Anulada") throw new InvalidOperationException("La factura ya está anulada.");
            if (factura.Estado != "Emitida" || string.IsNullOrWhiteSpace(factura.NCF)) throw new InvalidOperationException("Solo se pueden anular facturas emitidas.");
            AsegurarPeriodoAbierto(db, usuarioId, factura.Fecha);
            AsegurarPeriodoAbierto(db, usuarioId, _now());
            if (db.Table<NotaCredito>().ToList().Any(notaExistente => notaExistente.UsuarioId == usuarioId && notaExistente.FacturaRelacionadaId == factura.Id && !notaExistente.Anulada && !notaExistente.EsBorrador))
                throw new InvalidOperationException("La factura ya tiene notas de crédito emitidas. No se puede realizar una devolución completa automática porque duplicaría el ajuste fiscal; requiere revisión.");
            if (db.Table<NotaDebito>().Any(notaExistente => notaExistente.IdUsuario == usuarioId && notaExistente.IdFacturaOrigen == factura.Id && notaExistente.Estado == "A"))
                throw new InvalidOperationException("La factura ya tiene notas de débito emitidas. La devolución completa requiere revisión de esos ajustes antes de anularla.");
            var ajustesNcf = db.Find<NcfUsuario>(IdNcf(usuarioId, "B04"));
            if (ajustesNcf == null || !ajustesNcf.Activo) throw new InvalidOperationException("Este tipo de comprobante no está registrado.");
            var nota = new NotaCredito
            {
                Id = Guid.NewGuid(), NCF = GenerarSiguienteNCF(db, usuarioId, "B04"),
                Fecha = DateTime.UtcNow, FechaCreacion = DateTime.UtcNow,
                ClienteId = factura.ClienteId, ClienteNombre = factura.ClienteNombre, ClienteRnc = factura.ClienteRnc,
                FacturaRelacionadaId = factura.Id, Motivo = $"Anulación de factura {factura.NCF}: {motivo.Trim()}",
                Subtotal = factura.Subtotal + factura.RecargoMonto, Itbis = factura.Itbis, Total = factura.Total,
                UsuarioId = usuarioId
            };
            var itemsNota = factura.Items.ToList();
            if (factura.RecargoMonto > 0) itemsNota.Add(new ItemFactura { Descripcion = $"Recargo {factura.RecargoPorcentaje:0.##}%", Cantidad = 1, PrecioUnitario = factura.RecargoMonto });
            nota.Items = itemsNota;
            db.Insert(nota);
            RestituirInventarioVenta(db, factura.Items);
            factura.Anulada = true;
            factura.Estado = "Anulada";
            factura.FechaAnulacion = nota.Fecha;
            db.Update(factura);
            db.Insert(new RegistroFiscal
            {
                UsuarioId = usuarioId, TipoOperacion = "Venta", FacturaId = factura.Id.ToString("N"), TurnoId = factura.TurnoId,
                FechaComprobante = nota.Fecha, RncContraparte = factura.ClienteRnc, Ncf = nota.NCF, NcfModificado = factura.NCF,
                TipoBienServicio = "Pendiente de clasificación (nota de crédito)", TipoIngresoGasto = "Nota de crédito por anulación",
                MontoGravadoTotal = F(-factura.Items.Where(item => item.SujetoImpuesto).Sum(item => item.Subtotal)),
                MontoExento = F(-factura.Items.Where(item => !item.SujetoImpuesto).Sum(item => item.Subtotal)),
                TotalFacturado = F(-factura.Total), ItbisFacturado = F(-factura.Itbis), EstadoRevision = "Pendiente de revisión fiscal",
                FuenteJson = System.Text.Json.JsonSerializer.Serialize(itemsNota)
            });
            return nota;
        });
    }

    public async Task<AjustesUsuario> ObtenerAjustesAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await _db.FindAsync<AjustesUsuario>(usuarioId)
            ?? new AjustesUsuario
            {
                UsuarioId = usuarioId,
                NombreCompania = "Ponceano Servicios, S.R.L.",
                RncCompania = "1-32-58543-7",
                Telefonos = "1-849-531-3660 / 1-829-665-4316",
                CorreoFactura = "Ponceeanomultiservicios@gmail.com"
            };
    }

    public async Task GuardarAjustesAsync(AjustesUsuario ajustes)
    {
        ArgumentNullException.ThrowIfNull(ajustes);
        if (string.IsNullOrWhiteSpace(ajustes.UsuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(ajustes));
        await InitTablasDocumentosAsync();
        await _db.InsertOrReplaceAsync(ajustes);
    }

    public async Task<List<Articulo>> GetArticulosAsync()
    {
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<Articulo>("SELECT * FROM Articulo ORDER BY Descripcion COLLATE NOCASE");
    }

    public async Task GuardarArticuloAsync(Articulo articulo, string usuarioId = "", string nombreUsuario = "", string motivo = "")
    {
        ArgumentNullException.ThrowIfNull(articulo);
        if (string.IsNullOrWhiteSpace(articulo.Descripcion) || articulo.Descripcion.Trim().Length > 250)
            throw new InvalidOperationException("La descripción es obligatoria y no puede superar 250 caracteres.");
        if (articulo.Codigo?.Trim().Length > 50) throw new InvalidOperationException("El código no puede superar 50 caracteres.");
        if (articulo.Precio < 0 || articulo.Itbis is < 0 or > 100 || articulo.Stock < 0 || articulo.StockMinimo < 0 || articulo.UltimoCosto < 0) throw new InvalidOperationException("El precio, el ITBIS, el inventario, el stock mínimo y el costo deben ser válidos.");
        if (articulo.TipoBienServicio is not ("Bienes" or "Servicios" or "Pendiente")) throw new InvalidOperationException("La clasificación fiscal debe ser Bienes, Servicios o Pendiente.");
        articulo.Descripcion = articulo.Descripcion.Trim();
        articulo.Codigo = articulo.Codigo?.Trim() ?? string.Empty;
        await InitTablasDocumentosAsync();
        await EnTransaccionAsync(db =>
        {
            if (articulo.Id == 0)
            {
                db.Insert(articulo);
                return 0;
            }
            var anterior = db.Find<Articulo>(articulo.Id) ?? throw new InvalidOperationException("El artículo ya no existe.");
            void Registrar(string campo, string valorAnterior, string valorNuevo)
            {
                if (valorAnterior == valorNuevo) return;
                db.Insert(new HistorialCambioProducto
                {
                    IdProducto = articulo.Id, IdUsuario = usuarioId, NombreUsuario = nombreUsuario, Motivo = motivo?.Trim() ?? string.Empty,
                    CampoModificado = campo, ValorAnterior = valorAnterior, ValorNuevo = valorNuevo
                });
                db.Insert(new BitacoraAuditoria
                {
                    FechaHora = _now(), UsuarioId = usuarioId, Accion = "Cambio de producto", TipoEntidad = "Articulo", IdEntidad = articulo.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Detalle = $"{campo}: '{valorAnterior}' → '{valorNuevo}'. Motivo: {motivo?.Trim()}"
                });
            }
            Registrar("Codigo", anterior.Codigo, articulo.Codigo);
            Registrar("Descripcion", anterior.Descripcion, articulo.Descripcion);
            Registrar("PrecioVenta", F(anterior.Precio), F(articulo.Precio));
            Registrar("Costo", F(anterior.UltimoCosto), F(articulo.UltimoCosto));
            Registrar("StockManual", anterior.Stock.ToString(System.Globalization.CultureInfo.InvariantCulture), articulo.Stock.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Registrar("StockMinimo", anterior.StockMinimo.ToString(System.Globalization.CultureInfo.InvariantCulture), articulo.StockMinimo.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Registrar("TipoBienServicio", anterior.TipoBienServicio, articulo.TipoBienServicio);
            if (db.Update(articulo) == 0) throw new InvalidOperationException("No se pudo actualizar el artículo.");
            return 0;
        });
    }

    public async Task<int> EliminarArticuloAsync(int id)
    {
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db =>
        {
            var articulo = db.Find<Articulo>(id);
            if (articulo is null) return 0;
            var usado = db.Table<Factura>().ToList().Any(factura => factura.Items.Any(item => item.ArticuloId == id)) ||
                db.Table<DetallePedidoCompra>().ToList().Any(detalle => detalle.ArticuloId == id && db.Find<PedidoCompra>(detalle.PedidoId)?.Estado == "Pendiente");
            if (usado) throw new InvalidOperationException("No se puede eliminar un artículo vinculado a facturas o pedidos pendientes.");
            return db.Delete<Articulo>(id);
        });
    }

    public async Task<List<Articulo>> GetArticulosBajoMinimoAsync()
    {
        await InitTablasDocumentosAsync();
        return (await GetArticulosAsync()).Where(articulo => articulo.StockMinimo > 0 && articulo.Stock <= articulo.StockMinimo).ToList();
    }

    public async Task<List<HistorialCambioProducto>> ObtenerHistorialCambiosProductoAsync(int idProducto)
    {
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<HistorialCambioProducto>("SELECT * FROM HistorialCambioProducto WHERE IdProducto = ? ORDER BY FechaHora DESC", idProducto);
    }

    public async Task<List<PeriodoFiscal>> ObtenerPeriodosFiscalesAsync(int anio)
    {
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<PeriodoFiscal>("SELECT * FROM PeriodoFiscal WHERE Anio = ? ORDER BY Mes", anio);
    }

    public async Task<PeriodoFiscal?> ObtenerPeriodoFiscalAsync(string usuarioId, int anio, int mes)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        ValidarPeriodo(anio, mes);
        await InitTablasDocumentosAsync();
        var id = IdPeriodo(usuarioId, anio, mes);
        return await _db.FindAsync<PeriodoFiscal>(id);
    }

    public async Task<PeriodoFiscal> CerrarPeriodoFiscalAsync(string usuarioId, int anio, int mes, string nombreUsuario = "")
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        ValidarPeriodo(anio, mes);
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db =>
        {
            var id = IdPeriodo(usuarioId, anio, mes);
            var periodo = db.Find<PeriodoFiscal>(id) ?? new PeriodoFiscal
            {
                Id = id, UsuarioId = usuarioId, Anio = anio, Mes = mes,
                FechaInicio = new DateTime(anio, mes, 1, 0, 0, 0, DateTimeKind.Local), Estado = "A"
            };
            if (periodo.Estado == "C") throw new InvalidOperationException("El período ya está cerrado.");
            periodo.Estado = "C";
            periodo.FechaCierre = _now();
            periodo.FechaHoraCierre = _now();
            periodo.UsuarioCierra = usuarioId;
            if (db.Find<PeriodoFiscal>(id) is null) db.Insert(periodo);
            else db.Update(periodo);
            db.Insert(new BitacoraAuditoria
            {
                FechaHora = _now(), UsuarioId = usuarioId, Accion = "Cierre de período fiscal", TipoEntidad = "PeriodoFiscal", IdEntidad = id,
                Detalle = $"Usuario {nombreUsuario.Trim()} cerró el período {mes:D2}/{anio}."
            });
            return periodo;
        });
    }

    public async Task<List<ConfiguracionAlerta>> ObtenerConfiguracionesAlertasAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<ConfiguracionAlerta>("SELECT * FROM ConfiguracionAlerta WHERE UsuarioId = ? ORDER BY TipoAlerta", usuarioId);
    }

    public async Task GuardarConfiguracionesAlertasAsync(string usuarioId, IReadOnlyCollection<ConfiguracionAlerta> alertas)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        ArgumentNullException.ThrowIfNull(alertas);
        var tiposPermitidos = new[] { "Vencimiento DGII", "Stock bajo", "Serie NCF por agotarse", "Facturas pendientes" };
        if (alertas.Count != tiposPermitidos.Length || tiposPermitidos.Any(tipo => alertas.Count(alerta => alerta.TipoAlerta == tipo) != 1))
            throw new InvalidOperationException("Configura exactamente una fila para cada tipo de alerta.");
        foreach (var alerta in alertas)
        {
            if (!TimeOnly.TryParse(alerta.HoraAviso, System.Globalization.CultureInfo.InvariantCulture, out _))
                throw new InvalidOperationException($"La hora para «{alerta.TipoAlerta}» no es válida.");
            if (!string.IsNullOrWhiteSpace(alerta.FechaAviso) && !DateOnly.TryParseExact(alerta.FechaAviso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                throw new InvalidOperationException($"La fecha para «{alerta.TipoAlerta}» no es válida.");
        }
        await InitTablasDocumentosAsync();
        await EnTransaccionAsync(db =>
        {
            foreach (var alerta in alertas)
            {
                alerta.UsuarioId = usuarioId;
                if (alerta.IdAlerta == 0) db.Insert(alerta);
                else if (db.Update(alerta) == 0) throw new InvalidOperationException("No se pudo guardar una alerta.");
            }
            db.Insert(new BitacoraAuditoria { FechaHora = _now(), UsuarioId = usuarioId, Accion = "Configuración de alertas", TipoEntidad = "ConfiguracionAlerta", Detalle = "El usuario actualizó sus preferencias de alertas." });
            return 0;
        });
    }

    public async Task RegistrarEventoAuditoriaAsync(string usuarioId, string accion, string tipoEntidad, string idEntidad, string detalle)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(accion)) throw new ArgumentException("La acción es obligatoria.", nameof(accion));
        await InitTablasDocumentosAsync();
        await _db.InsertAsync(new BitacoraAuditoria { FechaHora = _now(), UsuarioId = usuarioId, Accion = accion, TipoEntidad = tipoEntidad ?? string.Empty, IdEntidad = idEntidad ?? string.Empty, Detalle = detalle ?? string.Empty });
    }

    public async Task RegistrarDevolucionInventarioAsync(DevolucionInventario devolucion)
    {
        ArgumentNullException.ThrowIfNull(devolucion);
        if (string.IsNullOrWhiteSpace(devolucion.UsuarioId) || devolucion.ArticuloId <= 0 || devolucion.Cantidad <= 0 || string.IsNullOrWhiteSpace(devolucion.Contraparte) || string.IsNullOrWhiteSpace(devolucion.Motivo))
            throw new InvalidOperationException("Completa usuario, contraparte, artículo, cantidad y motivo de la devolución.");
        if (devolucion.Direccion is not ("Cliente" or "Proveedor")) throw new ArgumentException("La dirección debe ser Cliente o Proveedor.", nameof(devolucion));
        await InitTablasDocumentosAsync();
        await EnTransaccionAsync(db =>
        {
            AsegurarPeriodoAbierto(db, devolucion.UsuarioId, devolucion.Fecha);
            var articulo = db.Find<Articulo>(devolucion.ArticuloId) ?? throw new InvalidOperationException("El artículo ya no existe.");
            var cambio = devolucion.Direccion == "Cliente" ? devolucion.Cantidad : -devolucion.Cantidad;
            var stock = checked(articulo.Stock + cambio);
            if (stock < 0) throw new InvalidOperationException($"No se puede devolver al proveedor más stock del disponible ({articulo.Stock}).");
            articulo.Stock = stock;
            db.Update(articulo);
            devolucion.Articulo = articulo.Descripcion;
            db.Insert(devolucion);
            db.Insert(new HistorialCambioProducto
            {
                IdProducto = articulo.Id, IdUsuario = devolucion.UsuarioId, NombreUsuario = devolucion.UsuarioId,
                CampoModificado = "Devolución de inventario", ValorAnterior = (stock - cambio).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ValorNuevo = stock.ToString(System.Globalization.CultureInfo.InvariantCulture), Motivo = $"{devolucion.Direccion}: {devolucion.Contraparte}. {devolucion.Motivo}"
            });
            db.Insert(new BitacoraAuditoria
            {
                FechaHora = _now(), UsuarioId = devolucion.UsuarioId, Accion = "Devolución de inventario", TipoEntidad = "Articulo", IdEntidad = articulo.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Detalle = $"{devolucion.Direccion}: {devolucion.Cantidad} × {articulo.Descripcion}. Stock: {stock - cambio} → {stock}. Motivo: {devolucion.Motivo}"
            });
            return 0;
        });
    }

    public async Task<string> CrearRespaldoBaseDatosAsync(string usuarioId, string carpetaDestino)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        ArgumentException.ThrowIfNullOrWhiteSpace(carpetaDestino);
        await InitTablasDocumentosAsync();
        Directory.CreateDirectory(carpetaDestino);
        var ruta = Path.Combine(carpetaDestino, $"Respaldo_Ponceano_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db3");
        await _db.ExecuteAsync("VACUUM INTO ?", ruta);
        try
        {
            await RegistrarEventoAuditoriaAsync(usuarioId, "Respaldo local creado", "BaseDatos", Path.GetFileName(ruta), $"Respaldo SQLite consistente: {ruta}");
            return ruta;
        }
        catch
        {
            if (File.Exists(ruta)) File.Delete(ruta);
            throw;
        }
    }

    public async Task<List<BitacoraAuditoria>> ObtenerBitacoraAuditoriaAsync(string usuarioId, int limite = 100)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        if (limite is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limite));
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<BitacoraAuditoria>("SELECT * FROM BitacoraAuditoria WHERE UsuarioId = ? ORDER BY FechaHora DESC LIMIT ?", usuarioId, limite);
    }

    public async Task<List<HistorialExportacionDGII>> ObtenerHistorialExportacionesDGIIAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<HistorialExportacionDGII>("SELECT * FROM HistorialExportacionDGII WHERE IdUsuario = ? ORDER BY FechaHora DESC", usuarioId);
    }

    public async Task RegistrarExportacionDGIIAsync(HistorialExportacionDGII exportacion)
    {
        ArgumentNullException.ThrowIfNull(exportacion);
        if (string.IsNullOrWhiteSpace(exportacion.IdUsuario) || exportacion.TipoReporte is not ("606" or "607"))
            throw new ArgumentException("El usuario y el tipo de reporte son obligatorios.", nameof(exportacion));
        await InitTablasDocumentosAsync();
        await _db.InsertAsync(exportacion);
    }

    public async Task<(long TamanoBytes, string Integridad)> ObtenerInformacionBaseDatosAsync()
    {
        await InitTablasDocumentosAsync();
        var tamano = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0L;
        var integridad = await _db.ExecuteScalarAsync<string>("PRAGMA quick_check");
        return (tamano, integridad);
    }

    public async Task OptimizarBaseDatosAsync()
    {
        await InitTablasDocumentosAsync();
        await _db.ExecuteAsync("PRAGMA optimize");
        await _db.ExecuteAsync("VACUUM");
    }

    public async Task<TurnoCaja?> ObtenerTurnoAbiertoAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await _db.FindWithQueryAsync<TurnoCaja>("SELECT * FROM TurnoCaja WHERE Estado = 'A' AND UsuarioAbre = ? ORDER BY HoraInicio DESC LIMIT 1", usuarioId);
    }

    public async Task<List<TurnoCaja>> ObtenerTurnosAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<TurnoCaja>("SELECT * FROM TurnoCaja WHERE UsuarioAbre = ? ORDER BY HoraInicio DESC", usuarioId);
    }

    public async Task<TurnoCaja> AbrirTurnoActualAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db => ObtenerOAbrirTurnoActual(db, usuarioId, _now()));
    }

    public async Task<TurnoCaja> ObtenerResumenTurnoAsync(string turnoId, string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(turnoId) || string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El turno y el usuario son obligatorios.");
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db => CalcularResumenTurno(db, turnoId, usuarioId));
    }

    public async Task<TurnoCaja> CerrarTurnoAsync(string turnoId, string usuarioId, string observaciones) =>
        await CerrarTurnoAsync(turnoId, usuarioId, observaciones, null, string.Empty);

    public async Task<TurnoCaja> CerrarTurnoAsync(string turnoId, string usuarioId, string observaciones, decimal? totalContado, string motivoDiferencia, decimal? totalEsperado = null)
    {
        if (string.IsNullOrWhiteSpace(turnoId) || string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El turno y el usuario son obligatorios.");
        if (totalContado < 0) throw new ArgumentOutOfRangeException(nameof(totalContado), "El total contado no puede ser negativo.");
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db =>
        {
            var turno = CalcularResumenTurno(db, turnoId, usuarioId);
            var totalSistema = decimal.Parse(turno.TotalGeneral, System.Globalization.CultureInfo.InvariantCulture);
            if (totalEsperado.HasValue && totalEsperado.Value != totalSistema)
                throw new InvalidOperationException("El total del turno cambió desde la vista previa. Vuelve a revisar el cuadre antes de cerrar.");
            turno.TotalSistema = turno.TotalGeneral;
            turno.TotalContado = F(totalContado ?? totalSistema);
            var diferencia = decimal.Parse(turno.TotalSistema, System.Globalization.CultureInfo.InvariantCulture) - decimal.Parse(turno.TotalContado, System.Globalization.CultureInfo.InvariantCulture);
            turno.Diferencia = F(diferencia);
            turno.MotivoDiferencia = diferencia == 0 ? string.Empty : motivoDiferencia?.Trim() ?? string.Empty;
            if (diferencia != 0 && turno.MotivoDiferencia.Length == 0)
                throw new InvalidOperationException($"Existe una diferencia de RD$ {Math.Abs(diferencia):N2}. Escribe el motivo antes de cerrar el turno.");
            if (diferencia != 0)
                throw new InvalidOperationException($"La diferencia de RD$ {Math.Abs(diferencia):N2} requiere autorización administrativa verificada. La autenticación actual no tiene roles de administrador configurados; el turno no se cerró.");
            turno.Observaciones = observaciones?.Trim() ?? string.Empty;
            turno.HoraCierre = _now();
            turno.UsuarioCierra = usuarioId;
            turno.Estado = "C";
            db.Update(turno);
            db.Insert(new BitacoraAuditoria
            {
                FechaHora = _now(), UsuarioId = usuarioId, Accion = "Cierre de turno", TipoEntidad = "TurnoCaja", IdEntidad = turno.Id,
                Detalle = $"Sistema: RD$ {turno.TotalSistema}; contado: RD$ {turno.TotalContado}; diferencia: RD$ {turno.Diferencia}; motivo: {turno.MotivoDiferencia}"
            });
            return turno;
        });
    }

    private static TurnoCaja CalcularResumenTurno(SQLiteConnection db, string turnoId, string usuarioId)
    {
        var turno = db.Find<TurnoCaja>(turnoId) ?? throw new InvalidOperationException("No se encontró el turno.");
        if (turno.UsuarioAbre != usuarioId) throw new InvalidOperationException("No tienes permiso para consultar este turno.");
        if (turno.Estado != "A") throw new InvalidOperationException("El turno ya está cerrado y no puede modificarse.");
        var facturas = db.Table<Factura>().Where(f => f.TurnoId == turno.Id && f.Estado == "Emitida" && !f.Anulada).ToList();
        turno.TotalSubtotal = Suma(facturas, factura => factura.Subtotal);
        turno.TotalItbis = Suma(facturas, factura => factura.Itbis);
        turno.TotalGeneral = Suma(facturas, factura => factura.Total);
        turno.TotalEfectivo = SumaMetodo(facturas, "Efectivo");
        turno.TotalTarjeta = SumaMetodo(facturas, "Tarjeta");
        turno.TotalTransferencia = SumaMetodo(facturas, "Transferencia");
        turno.TotalCredito = SumaMetodo(facturas, "Crédito");
        turno.CantidadFacturas = facturas.Count;
        return turno;
    }

    public async Task<List<RegistroFiscal>> ObtenerRegistrosFiscalesAsync(string usuarioId, string tipoOperacion, DateTime desde, DateTime hasta, string? turnoId = null)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        if (tipoOperacion is not ("Venta" or "Compra")) throw new ArgumentException("La operación debe ser Venta o Compra.", nameof(tipoOperacion));
        await InitTablasDocumentosAsync();
        var registros = await _db.QueryAsync<RegistroFiscal>("SELECT * FROM RegistroFiscal WHERE UsuarioId = ? AND TipoOperacion = ? ORDER BY FechaComprobante, Ncf", usuarioId, tipoOperacion);
        var inicioLocal = desde.ToLocalTime();
        var finLocal = hasta.ToLocalTime();
        return registros.Where(registro => registro.FechaComprobante.ToLocalTime() >= inicioLocal && registro.FechaComprobante.ToLocalTime() < finLocal &&
            (string.IsNullOrWhiteSpace(turnoId) || registro.TurnoId == turnoId)).ToList();
    }

    public async Task GuardarCompraCapturadaAsync(RegistroFiscal registro)
    {
        ArgumentNullException.ThrowIfNull(registro);
        if (string.IsNullOrWhiteSpace(registro.UsuarioId)) throw new ArgumentException("Inicia sesión antes de cargar una compra al 606.", nameof(registro));
        if (registro.TipoOperacion != "Compra") throw new ArgumentException("La captura inteligente solo puede crear registros de compra 606.", nameof(registro));
        if (!System.Text.RegularExpressions.Regex.IsMatch(registro.RncContraparte?.Trim() ?? string.Empty, @"^(?:[0-9]{9}|[0-9]{11})$"))
            throw new ArgumentException("El RNC o cédula del proveedor debe tener 9 u 11 dígitos.", nameof(registro));
        if (string.IsNullOrWhiteSpace(registro.RazonSocialContraparte)) throw new ArgumentException("La razón social del proveedor es obligatoria.", nameof(registro));
        if (!System.Text.RegularExpressions.Regex.IsMatch(registro.TipoBienServicio?.Trim() ?? string.Empty, @"^(?:0[1-9]|1[01])$"))
            throw new ArgumentException("Selecciona una clasificación oficial de compra 606 (01-11).", nameof(registro));
        if (!System.Text.RegularExpressions.Regex.IsMatch(registro.Ncf?.Trim() ?? string.Empty, @"^(?:[AB][0-9]{10}|E[0-9]{12}|[ACDF-Z][0-9]{19}|[A-Z0-9]{3}-[0-9]{3,4}-[0-9]{7,10})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("El NCF no tiene un formato reconocido.", nameof(registro));
        if (registro.FechaComprobante == default) throw new ArgumentException("Selecciona la fecha del comprobante.", nameof(registro));
        if (!new[] { "Efectivo", "Cheque", "Transferencia", "Depósito", "Deposito", "Tarjeta", "Crédito", "Credito", "Permuta", "Otro" }
                .Contains(registro.MetodoPago?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Selecciona un método de pago compatible con el Formato 606.", nameof(registro));
        if (!decimal.TryParse(registro.MontoGravadoTotal, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var gravado) || gravado < 0 ||
            !decimal.TryParse(registro.MontoExento, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var exento) || exento < 0 ||
            !decimal.TryParse(registro.ItbisFacturado, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var itbis) || itbis < 0 ||
            !decimal.TryParse(registro.TotalFacturado, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var total) || total <= 0 ||
            decimal.Round(gravado, 2) != gravado || decimal.Round(exento, 2) != exento || decimal.Round(itbis, 2) != itbis || decimal.Round(total, 2) != total || gravado + exento + itbis != total)
            throw new ArgumentException("El total debe coincidir con el monto gravado, el monto exento y el ITBIS; revisa los importes del OCR.", nameof(registro));
        if (!decimal.TryParse(registro.MontoGravadoBienes, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var baseBienes) ||
            !decimal.TryParse(registro.MontoGravadoServicios, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var baseServicios) ||
            baseBienes + baseServicios != gravado)
            throw new ArgumentException("Las bases gravadas de bienes y servicios deben sumar el monto gravado.", nameof(registro));

        var rncProveedor = registro.RncContraparte?.Trim() ?? string.Empty;
        var razonSocial = registro.RazonSocialContraparte?.Trim() ?? string.Empty;
        var ncf = registro.Ncf?.Trim().ToUpperInvariant() ?? string.Empty;
        await InitTablasDocumentosAsync();
        await EnTransaccionAsync(db =>
        {
            AsegurarPeriodoAbierto(db, registro.UsuarioId, registro.FechaComprobante);
            var duplicados = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM RegistroFiscal WHERE UsuarioId = ? AND TipoOperacion = 'Compra' AND UPPER(TRIM(Ncf)) = ? AND RncContraparte = ?",
                registro.UsuarioId, ncf, rncProveedor);
            if (duplicados > 0) throw new InvalidOperationException($"Ya existe una compra 606 para el RNC y NCF {ncf}.");
            registro.RncContraparte = rncProveedor;
            registro.RazonSocialContraparte = razonSocial;
            registro.Ncf = ncf;
            registro.EstadoRevision = "Pendiente de revisión fiscal";
            registro.EstadoValidacionDgii = "Pendiente";
            db.Insert(registro);
            db.Insert(new BitacoraAuditoria
            {
                FechaHora = _now(), UsuarioId = registro.UsuarioId, Accion = "Captura inteligente de factura de compra para 606",
                TipoEntidad = "RegistroFiscal", IdEntidad = registro.Id,
                Detalle = $"NCF {registro.Ncf}; RNC {registro.RncContraparte}; origen OCR con validación manual del usuario."
            });
            return 0;
        });
    }

    public async Task<IReadOnlyList<ProblemaRegistroFiscal>> RevalidarRegistrosFiscalesAsync(string usuarioId, string tipoReporte, int anio, int mes)
    {
        ValidarPeriodoCorreccion(usuarioId, tipoReporte, anio, mes);
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync<IReadOnlyList<ProblemaRegistroFiscal>>(db =>
        {
            var inicio = new DateTime(anio, mes, 1, 0, 0, 0, DateTimeKind.Local);
            var operacion = tipoReporte == "606" ? "Compra" : "Venta";
            var registros = db.Query<RegistroFiscal>("SELECT * FROM RegistroFiscal WHERE UsuarioId = ? AND TipoOperacion = ? ORDER BY FechaComprobante, Ncf", usuarioId, operacion)
                .Where(registro => registro.FechaComprobante.ToLocalTime() >= inicio && registro.FechaComprobante.ToLocalTime() < inicio.AddMonths(1));
            var problemas = new List<ProblemaRegistroFiscal>();
            foreach (var registro in registros)
            {
                var errores = FormatoEnvioDgiiService.ValidarRegistro(registro, tipoReporte, inicio);
                var estado = errores.Count == 0 ? "Validado correctamente (validación local)" : "Con error (validación local)";
                if (!string.Equals(registro.EstadoValidacionDgii, estado, StringComparison.Ordinal))
                {
                    db.Execute("UPDATE RegistroFiscal SET EstadoValidacionDgii = ? WHERE Id = ?", estado, registro.Id);
                    registro.EstadoValidacionDgii = estado;
                }
                if (errores.Count > 0) problemas.Add(new ProblemaRegistroFiscal(registro, errores));
            }
            return problemas;
        });
    }

    public async Task<HashSet<string>> ObtenerNcfIncluidosEnEnviosAsync(string usuarioId, string tipoReporte, int anio, int mes)
    {
        ValidarPeriodoCorreccion(usuarioId, tipoReporte, anio, mes);
        await InitTablasDocumentosAsync();
        var envios = await _db.QueryAsync<HistorialExportacionDGII>(
            "SELECT * FROM HistorialExportacionDGII WHERE IdUsuario = ? AND TipoReporte = ? AND PeriodoAnio = ? AND PeriodoMes = ? AND Estado LIKE ?",
            usuarioId, tipoReporte, anio, mes, "Copia archivada tras confirmación manual de envío%");
        return ObtenerNcfEnviados(envios, tipoReporte);
    }

    public async Task GuardarCorreccionRegistroFiscalAsync(string usuarioId, string tipoReporte, int anio, int mes, string registroId,
        string rnc, string razonSocial, string tipoFiscal, decimal montoGravado, decimal itbis, decimal total,
        decimal? montoServicios = null, decimal? montoBienes = null, decimal? montoExento = null)
    {
        ValidarPeriodoCorreccion(usuarioId, tipoReporte, anio, mes);
        if (string.IsNullOrWhiteSpace(registroId)) throw new ArgumentException("Selecciona un registro fiscal.", nameof(registroId));
        var rncValidado = rnc?.Trim() ?? string.Empty;
        var tipoFiscalValidado = tipoFiscal?.Trim() ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(rncValidado, @"^(?:[0-9]{9}|[0-9]{11})$"))
            throw new ArgumentException("El RNC o cédula debe tener 9 u 11 dígitos.", nameof(rnc));
        if (string.IsNullOrWhiteSpace(razonSocial)) throw new ArgumentException("La razón social es obligatoria.", nameof(razonSocial));
        var codigoValido = tipoReporte == "607"
            ? System.Text.RegularExpressions.Regex.IsMatch(tipoFiscalValidado, @"^0[1-6]$")
            : System.Text.RegularExpressions.Regex.IsMatch(tipoFiscalValidado, @"^(?:0[1-9]|1[01])$");
        if (!codigoValido) throw new ArgumentException("Selecciona un código fiscal válido para el formato.", nameof(tipoFiscal));
        if (montoGravado < 0 || itbis < 0 || total < 0 || decimal.Round(montoGravado, 2) != montoGravado || decimal.Round(itbis, 2) != itbis || decimal.Round(total, 2) != total)
            throw new ArgumentException("Los montos deben ser numéricos, no negativos y tener hasta dos decimales.");
        if (!montoExento.HasValue || montoExento.Value < 0 || decimal.Round(montoExento.Value, 2) != montoExento.Value)
            throw new ArgumentException("El monto exento debe ser no negativo y tener hasta dos decimales.", nameof(montoExento));
        if (tipoReporte == "606" && (!montoServicios.HasValue || !montoBienes.HasValue ||
            montoServicios.Value < 0 || montoBienes.Value < 0 || montoServicios.Value > montoGravado || montoBienes.Value != montoGravado - montoServicios.Value ||
            decimal.Round(montoServicios.Value, 2) != montoServicios.Value || decimal.Round(montoBienes.Value, 2) != montoBienes.Value))
            throw new ArgumentException("Para el Formato 606, los montos de servicios y bienes deben sumar el gravado total.");

        await InitTablasDocumentosAsync();
        await EnTransaccionAsync(db =>
        {
            var operacion = tipoReporte == "606" ? "Compra" : "Venta";
            var inicio = new DateTime(anio, mes, 1, 0, 0, 0, DateTimeKind.Local);
            var registro = db.Find<RegistroFiscal>(registroId) ?? throw new InvalidOperationException("No se encontró el registro fiscal seleccionado.");
            if (registro.UsuarioId != usuarioId || registro.TipoOperacion != operacion ||
                registro.FechaComprobante.ToLocalTime() < inicio || registro.FechaComprobante.ToLocalTime() >= inicio.AddMonths(1))
                throw new InvalidOperationException("El registro no pertenece a esta cuenta, formato o período.");
            if (RegistroIncluidoEnEnvio(db, usuarioId, tipoReporte, anio, mes, registro.Ncf))
                throw new InvalidOperationException($"El registro {registro.Ncf} ya está incluido en un envío confirmado; no se puede modificar.");

            registro.RncContraparte = rncValidado;
            registro.RazonSocialContraparte = razonSocial.Trim();
            registro.MontoGravadoTotal = montoGravado.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            registro.ItbisFacturado = itbis.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            registro.TotalFacturado = total.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            registro.MontoExento = montoExento.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            if (tipoReporte == "606")
            {
                registro.TipoBienServicio = tipoFiscalValidado;
                registro.MontoGravadoServicios = montoServicios!.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                registro.MontoGravadoBienes = montoBienes!.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            }
            else registro.TipoIngresoGasto = tipoFiscalValidado;
            registro.EstadoValidacionDgii = "Pendiente";
            registro.EstadoRevision = "Corregido para exportación fiscal";
            db.Update(registro);
            db.Insert(new BitacoraAuditoria
            {
                FechaHora = _now(), UsuarioId = usuarioId, Accion = $"Corrección de registro fiscal {tipoReporte}",
                TipoEntidad = "RegistroFiscal", IdEntidad = registro.Id,
                Detalle = $"NCF {registro.Ncf}; RNC {rnc}; clasificación {tipoFiscal}; monto gravado {registro.MontoGravadoTotal}; ITBIS {registro.ItbisFacturado}; total {registro.TotalFacturado}"
            });
            return true;
        });
    }

    private static bool RegistroIncluidoEnEnvio(SQLiteConnection db, string usuarioId, string tipoReporte, int anio, int mes, string ncf)
    {
        var envios = db.Table<HistorialExportacionDGII>().Where(historial => historial.IdUsuario == usuarioId &&
            historial.TipoReporte == tipoReporte && historial.PeriodoAnio == anio && historial.PeriodoMes == mes &&
            historial.Estado.StartsWith("Copia archivada tras confirmación manual de envío", StringComparison.Ordinal)).ToList();
        return ObtenerNcfEnviados(envios, tipoReporte).Contains(ncf);
    }

    private static HashSet<string> ObtenerNcfEnviados(IEnumerable<HistorialExportacionDGII> envios, string tipoReporte)
    {
        var ncfEnviados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnaNcf = tipoReporte == "606" ? 3 : 2;
        foreach (var envio in envios)
        {
            if (!File.Exists(envio.RutaGuardado))
                throw new InvalidOperationException($"No se puede verificar el archivo archivado {envio.NombreArchivo}; por seguridad, no se permite editar registros de este período.");
            var encabezado = true;
            var filasLeidas = 0;
            var filasDeclaradas = -1;
            foreach (var linea in File.ReadLines(envio.RutaGuardado))
            {
                if (string.IsNullOrWhiteSpace(linea)) continue;
                var campos = linea.Split('|');
                if (encabezado)
                {
                    if (campos.Length != 3 || !int.TryParse(campos[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out filasDeclaradas) || filasDeclaradas <= 0)
                        throw new InvalidOperationException($"El archivo archivado {envio.NombreArchivo} tiene un encabezado inválido; se bloqueó la edición por seguridad.");
                    encabezado = false;
                    continue;
                }
                if (campos.Length != 23) throw new InvalidOperationException($"El archivo archivado {envio.NombreArchivo} contiene una fila no verificable; se bloqueó la edición por seguridad.");
                filasLeidas++;
                if (!string.IsNullOrWhiteSpace(campos[columnaNcf])) ncfEnviados.Add(campos[columnaNcf]);
            }
            if (encabezado || filasLeidas != filasDeclaradas)
                throw new InvalidOperationException($"El archivo archivado {envio.NombreArchivo} está incompleto; se bloqueó la edición por seguridad.");
        }
        return ncfEnviados;
    }

    private static void ValidarPeriodoCorreccion(string usuarioId, string tipoReporte, int anio, int mes)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("Inicia sesión antes de corregir registros.", nameof(usuarioId));
        if (tipoReporte is not ("606" or "607")) throw new ArgumentException("El formato debe ser 606 o 607.", nameof(tipoReporte));
        if (anio is < 2000 or > 9998 || mes is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(mes), "Selecciona un período válido.");
    }

    public async Task<PedidoCompra> CrearPedidoCompraAsync(string usuarioId, IReadOnlyCollection<DetallePedidoCompra> detalles)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        if (detalles.Count == 0 || detalles.Any(detalle => detalle.ArticuloId <= 0 || detalle.CantidadPedida <= 0))
            throw new InvalidOperationException("El pedido necesita artículos y cantidades mayores que cero.");
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db =>
        {
            var pedido = new PedidoCompra { UsuarioId = usuarioId };
            db.Insert(pedido);
            foreach (var detalle in detalles)
            {
                var articulo = db.Find<Articulo>(detalle.ArticuloId) ?? throw new InvalidOperationException($"No existe el artículo {detalle.DescripcionArticulo}.");
                db.Insert(new DetallePedidoCompra { PedidoId = pedido.Id, ArticuloId = articulo.Id, DescripcionArticulo = articulo.Descripcion, CantidadPedida = detalle.CantidadPedida, CostoUnitario = articulo.UltimoCosto.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) });
            }
            return pedido;
        });
    }

    public async Task<List<PedidoCompra>> ObtenerPedidosCompraPendientesAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<PedidoCompra>("SELECT * FROM PedidoCompra WHERE UsuarioId = ? AND Estado = 'Pendiente' ORDER BY Fecha DESC", usuarioId);
    }

    public async Task<List<DetallePedidoCompra>> ObtenerDetallesPedidoCompraAsync(string pedidoId)
    {
        await InitTablasDocumentosAsync();
        return await _db.QueryAsync<DetallePedidoCompra>("SELECT * FROM DetallePedidoCompra WHERE PedidoId = ? ORDER BY DescripcionArticulo", pedidoId);
    }

    public async Task ActualizarDetallePedidoCompraAsync(DetallePedidoCompra detalle, string usuarioId)
    {
        await InitTablasDocumentosAsync();
        await EnTransaccionAsync(db =>
        {
            var pedido = db.Find<PedidoCompra>(detalle.PedidoId);
            var existente = db.Find<DetallePedidoCompra>(detalle.Id);
            if (pedido == null || pedido.UsuarioId != usuarioId || pedido.Estado != "Pendiente" || existente?.PedidoId != pedido.Id)
                throw new InvalidOperationException("El pedido o el detalle ya no está disponible para modificar.");
            if (detalle.CantidadPedida < 1 || !decimal.TryParse(detalle.CostoUnitario, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var costo) || costo < 0)
                throw new InvalidOperationException("La cantidad y el costo deben ser válidos.");
            db.Update(detalle);
            return 0;
        });
    }

    public async Task<PedidoCompra> RecibirPedidoCompraAsync(string pedidoId, string usuarioId, string? proveedor, string? rnc, string? ncf, DateTime fecha, string? metodoPago, decimal montoGravado, decimal montoExento, decimal itbis, decimal otrosImpuestos, decimal total)
    {
        var proveedorNormalizado = proveedor?.Trim() ?? string.Empty;
        var rncNormalizado = rnc?.Trim() ?? string.Empty;
        var ncfNormalizado = ncf?.Trim() ?? string.Empty;
        var metodoPagoNormalizado = metodoPago?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(proveedorNormalizado)) throw new InvalidOperationException("El nombre del proveedor es obligatorio.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(rncNormalizado, "^(?:[0-9]{9}|[0-9]{11})$")) throw new InvalidOperationException("El RNC/cédula del proveedor debe contener 9 u 11 dígitos.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(ncfNormalizado, "^(?:B[0-9]{10}|B(?:0[1-6]|1[456])[0-9]{8}|E[0-9]{12})$")) throw new InvalidOperationException("El NCF no cumple un formato reconocido.");
        if (string.IsNullOrWhiteSpace(proveedorNormalizado) || total < 0 || montoGravado < 0 || montoExento < 0 || itbis < 0 || otrosImpuestos < 0 || Math.Abs(montoGravado + montoExento + itbis + otrosImpuestos - total) > 0.02m)
            throw new InvalidOperationException("Revisa proveedor y montos: el total debe coincidir con base gravada, base exenta, ITBIS y otros impuestos.");
        await InitTablasDocumentosAsync();
        return await EnTransaccionAsync(db =>
        {
            var pedido = db.Find<PedidoCompra>(pedidoId) ?? throw new InvalidOperationException("No se encontró el pedido.");
            if (pedido.UsuarioId != usuarioId || pedido.Estado != "Pendiente") throw new InvalidOperationException("El pedido ya fue recibido o no pertenece a este usuario.");
            AsegurarPeriodoAbierto(db, usuarioId, fecha);
            if (db.Table<RegistroFiscal>().Any(registro => registro.UsuarioId == usuarioId && registro.TipoOperacion == "Compra" && registro.Ncf == ncfNormalizado && registro.RncContraparte == rncNormalizado))
                throw new InvalidOperationException("Ya existe un registro de compra para ese RNC y NCF.");
            var detalles = db.Table<DetallePedidoCompra>().Where(detalle => detalle.PedidoId == pedido.Id).ToList();
            if (detalles.Count == 0) throw new InvalidOperationException("El pedido no contiene artículos.");
            foreach (var detalle in detalles)
            {
                var articulo = db.Find<Articulo>(detalle.ArticuloId) ?? throw new InvalidOperationException($"El artículo {detalle.DescripcionArticulo} ya no existe.");
                if (detalle.CantidadPedida < 1 || !decimal.TryParse(detalle.CostoUnitario, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var costo) || costo < 0)
                    throw new InvalidOperationException("El pedido contiene una cantidad o costo inválido.");
                articulo.Stock = checked(articulo.Stock + detalle.CantidadPedida);
                articulo.UltimoCosto = costo;
                db.Update(articulo);
                detalle.CantidadRecibida = detalle.CantidadPedida;
                db.Update(detalle);
            }
            pedido.ProveedorNombre = proveedorNormalizado;
            pedido.ProveedorRnc = rncNormalizado;
            pedido.NcfProveedor = ncfNormalizado;
            pedido.Estado = "Recibido";
            pedido.FechaRecepcion = fecha;
            db.Update(pedido);
            db.Insert(new RegistroFiscal
            {
                UsuarioId = usuarioId, TipoOperacion = "Compra", PedidoId = pedido.Id, FechaComprobante = fecha,
                TurnoId = ObtenerTurnoAbiertoParaFecha(db, usuarioId, _now())?.Id ?? string.Empty,
                RncContraparte = rncNormalizado, RazonSocialContraparte = proveedorNormalizado, Ncf = ncfNormalizado,
                TipoBienServicio = "Bienes de inventario (pedido recibido)", TipoIngresoGasto = "Compra de bienes",
                MontoGravadoBienes = F(montoGravado), MontoGravadoTotal = F(montoGravado), MontoExento = F(montoExento),
                ItbisFacturado = F(itbis), ItbisRetenido = string.Empty, IsrRetenido = string.Empty, OtrosImpuestos = F(otrosImpuestos),
                PropinaLegal = string.Empty, TotalFacturado = F(total), CondicionPago = metodoPagoNormalizado,
                MetodoPago = metodoPagoNormalizado, EstadoRevision = "Pendiente de revisión fiscal"
            });
            return pedido;
        });
    }

    private static TurnoCaja? ObtenerTurnoAbiertoParaFecha(SQLiteConnection db, string usuarioId, DateTime ahora)
    {
        var numero = DeterminarNumeroTurno(ahora);
        return numero == 0 ? null : db.Table<TurnoCaja>().FirstOrDefault(t => t.UsuarioAbre == usuarioId && t.Fecha == ahora.Date && t.NumeroTurno == numero && t.Estado == "A");
    }

    public static int DeterminarNumeroTurno(DateTime fechaHora) => fechaHora.TimeOfDay >= TimeSpan.FromHours(8) && fechaHora.TimeOfDay < TimeSpan.FromHours(15) ? 1
        : fechaHora.TimeOfDay >= TimeSpan.FromHours(15) ? 2 : 0;

    private static TurnoCaja ObtenerOAbrirTurnoActual(SQLiteConnection db, string usuarioId, DateTime ahora)
    {
        var numero = DeterminarNumeroTurno(ahora);
        if (numero == 0) throw new InvalidOperationException("No hay un turno programado entre medianoche y las 8:00 a. m.");
        var turno = db.Table<TurnoCaja>().FirstOrDefault(t => t.Fecha == ahora.Date && t.NumeroTurno == numero && t.UsuarioAbre == usuarioId);
        if (turno?.Estado == "C") throw new InvalidOperationException("El turno de hoy ya fue cerrado; no se pueden emitir más facturas en él.");
        if (turno != null) return turno;
        if (db.Table<TurnoCaja>().Any(t => t.UsuarioAbre == usuarioId && t.Estado == "A"))
            throw new InvalidOperationException("Cierra el turno anterior antes de abrir o facturar en uno nuevo.");
        turno = new TurnoCaja { Fecha = ahora.Date, NumeroTurno = numero, HoraInicio = ahora, UsuarioAbre = usuarioId };
        db.Insert(turno);
        return turno;
    }

    private static void DescontarInventarioVenta(SQLiteConnection db, IEnumerable<ItemFactura> items)
    {
        foreach (var grupo in items.Where(item => item.ArticuloId.HasValue).GroupBy(item => item.ArticuloId!.Value))
        {
            var cantidad = grupo.Sum(item => item.Cantidad);
            if (cantidad <= 0) throw new InvalidOperationException("La cantidad de un artículo vinculado al inventario debe ser mayor que cero.");
            var articulo = db.Find<Articulo>(grupo.Key) ?? throw new InvalidOperationException("Un artículo asociado a la factura ya no existe en el catálogo.");
            if (articulo.Stock < cantidad) throw new InvalidOperationException($"Inventario insuficiente para {articulo.Descripcion}: disponibles {articulo.Stock}, solicitados {cantidad}.");
            articulo.Stock -= cantidad;
            db.Update(articulo);
        }
    }

    private static void RestituirInventarioVenta(SQLiteConnection db, IEnumerable<ItemFactura> items)
    {
        foreach (var grupo in items.Where(item => item.ArticuloId.HasValue).GroupBy(item => item.ArticuloId!.Value))
        {
            var articulo = db.Find<Articulo>(grupo.Key) ?? throw new InvalidOperationException("No se pudo devolver el stock porque falta un artículo vinculado a la factura.");
            articulo.Stock = checked(articulo.Stock + grupo.Sum(item => item.Cantidad));
            db.Update(articulo);
        }
    }

    private static RegistroFiscal CrearRegistroFiscalExistente(Factura factura) => new()
    {
        UsuarioId = factura.UsuarioId, TipoOperacion = factura.TipoOperacion, FacturaId = factura.Id.ToString("N"), TurnoId = factura.TurnoId,
        FechaComprobante = factura.Fecha, RncContraparte = factura.ClienteRnc, RazonSocialContraparte = factura.ClienteNombre, Ncf = factura.NCF,
        TipoBienServicio = "Pendiente de clasificación", TipoIngresoGasto = "Pendiente de revisión",
        MontoGravadoBienes = string.Empty, MontoGravadoServicios = string.Empty, MontoGravadoTotal = string.Empty,
        MontoExento = string.Empty, TotalFacturado = F(factura.Total), ItbisFacturado = F(factura.Itbis),
        ItbisRetenido = string.Empty, IsrRetenido = string.Empty, OtrosImpuestos = string.Empty, PropinaLegal = string.Empty,
        CondicionPago = factura.EstadoPago, MetodoPago = factura.MetodoPago, EstadoRevision = "Histórico: pendiente de revisión fiscal",
        FuenteJson = factura.BorradorFiscalJson
    };

    private static RegistroFiscal CrearRegistroFiscalNotaCredito(NotaCredito nota, Factura? factura) => new()
    {
        UsuarioId = nota.UsuarioId, TipoOperacion = "Venta", FacturaId = factura?.Id.ToString("N") ?? string.Empty,
        TurnoId = factura?.TurnoId ?? string.Empty, FechaComprobante = nota.Fecha, RncContraparte = nota.ClienteRnc,
        RazonSocialContraparte = nota.ClienteNombre, Ncf = nota.NCF, NcfModificado = factura?.NCF ?? string.Empty,
        TipoBienServicio = "Pendiente de clasificación (nota de crédito)", TipoIngresoGasto = "Nota de crédito",
        MontoGravadoBienes = string.Empty, MontoGravadoServicios = string.Empty,
        MontoGravadoTotal = F(-nota.Items.Where(item => item.SujetoImpuesto).Sum(item => item.Subtotal)),
        MontoExento = F(-nota.Items.Where(item => !item.SujetoImpuesto).Sum(item => item.Subtotal)),
        TotalFacturado = F(-nota.Total), ItbisFacturado = F(-nota.Itbis), ItbisRetenido = "0.00", IsrRetenido = "0.00",
        OtrosImpuestos = "0.00", PropinaLegal = "0.00", EstadoRevision = "Pendiente de revisión fiscal",
        FuenteJson = nota.ItemsJson
    };

    private static RegistroFiscal CrearRegistroFiscalVenta(SQLiteConnection db, Factura factura)
    {
        var items = factura.Items;
        var tipos = items.Select(item => item.ArticuloId is int articuloId ? db.Find<Articulo>(articuloId)?.TipoBienServicio : "Pendiente").ToList();
        var clasificacionPendiente = items.Count == 0 || tipos.Any(tipo => tipo is not ("Bienes" or "Servicios"));
        var baseBienes = items.Where((item, index) => item.SujetoImpuesto && tipos[index] == "Bienes").Sum(item => item.Subtotal);
        var baseServicios = items.Where((item, index) => item.SujetoImpuesto && tipos[index] == "Servicios").Sum(item => item.Subtotal);
        var baseGravada = items.Where(item => item.SujetoImpuesto).Sum(item => item.Subtotal);
        var exento = items.Where(item => !item.SujetoImpuesto).Sum(item => item.Subtotal);
        return new RegistroFiscal
        {
            UsuarioId = factura.UsuarioId, TipoOperacion = "Venta", FacturaId = factura.Id.ToString("N"), TurnoId = factura.TurnoId,
            FechaComprobante = factura.Fecha, RncContraparte = factura.ClienteRnc, RazonSocialContraparte = factura.ClienteNombre, Ncf = factura.NCF,
            TipoBienServicio = clasificacionPendiente ? "Pendiente de clasificación" : "Bienes y servicios clasificados",
            TipoIngresoGasto = "Venta de bienes y servicios",
            MontoGravadoBienes = clasificacionPendiente ? string.Empty : F(baseBienes),
            MontoGravadoServicios = clasificacionPendiente ? string.Empty : F(baseServicios), MontoGravadoTotal = F(baseGravada),
            MontoExento = F(exento), TotalFacturado = F(factura.Total), ItbisFacturado = F(factura.Itbis),
            ItbisRetenido = "0.00", IsrRetenido = "0.00", OtrosImpuestos = "0.00", PropinaLegal = "0.00",
            CondicionPago = factura.EstadoPago, MetodoPago = factura.MetodoPago,
            EstadoRevision = clasificacionPendiente ? "Pendiente de clasificación DGII" : "Pendiente de revisión fiscal",
            FuenteJson = System.Text.Json.JsonSerializer.Serialize(items)
        };
    }

    private static RegistroFiscal CrearRegistroFiscalImportado(Factura factura) => new()
    {
        UsuarioId = factura.UsuarioId, TipoOperacion = factura.TipoOperacion, FacturaId = factura.Id.ToString("N"), FechaComprobante = factura.Fecha,
        RncContraparte = factura.ClienteRnc, RazonSocialContraparte = factura.ClienteNombre, Ncf = factura.NCF, TotalFacturado = F(factura.Total), ItbisFacturado = F(factura.Itbis),
        MontoGravadoBienes = string.Empty, MontoGravadoServicios = string.Empty, MontoGravadoTotal = string.Empty, MontoExento = string.Empty,
        ItbisRetenido = string.Empty, IsrRetenido = string.Empty, OtrosImpuestos = string.Empty, PropinaLegal = string.Empty,
        TipoBienServicio = "Pendiente de clasificación", TipoIngresoGasto = "Pendiente de revisión", CondicionPago = factura.EstadoPago,
        MetodoPago = factura.MetodoPago, EstadoRevision = "Importado: pendiente de revisión fiscal", FuenteJson = factura.BorradorFiscalJson
    };

    private static string Suma(IEnumerable<Factura> facturas, Func<Factura, decimal> valor) => F(facturas.Sum(valor));
    private static string SumaMetodo(IEnumerable<Factura> facturas, string metodo) => F(facturas.Where(factura => factura.MetodoPago.Contains(metodo, StringComparison.OrdinalIgnoreCase)).Sum(factura => factura.Total));
    private static string F(decimal valor) => valor.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<ResumenMensual> ObtenerResumenMensualAsync(string usuarioId)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        var ahora = _now();
        var inicio = new DateTime(ahora.Year, ahora.Month, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var cotizaciones = await GetCotizacionesAsync(usuarioId);
        var facturas = await GetFacturasAsync(usuarioId);
        var mes = facturas.Where(f => f.NCF.Length > 0 && (f.Estado == "Emitida" || f.Estado == "Anulada") && f.Fecha >= inicio).ToList();
        var notas = (await GetAllNotasCreditoAsync(usuarioId)).Where(n => !n.Anulada && !n.EsBorrador && n.Fecha >= inicio).ToList();
        return new ResumenMensual
        {
            Cotizaciones = cotizaciones.Count(c => c.Fecha >= inicio),
            Facturas = mes.Count,
            TotalVentas = mes.Sum(f => f.Total) - notas.Sum(n => n.Total),
            TotalItbis = mes.Sum(f => f.Itbis) - notas.Sum(n => n.Itbis)
        };
    }

    public async Task<string> GenerarSiguienteNCFAsync(string usuarioId, string tipo)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        await ObtenerConfiguracionNcfAsync(usuarioId);
        return await EnTransaccionAsync(db => GenerarSiguienteNCF(db, usuarioId, tipo));
    }

    public async Task<string> PrevisualizarNCFAsync(string usuarioId, string tipo)
    {
        if (string.IsNullOrWhiteSpace(usuarioId)) throw new ArgumentException("El identificador de usuario es obligatorio.", nameof(usuarioId));
        await InitTablasDocumentosAsync();
        await ObtenerConfiguracionNcfAsync(usuarioId);
        var config = await _db.FindAsync<NcfUsuario>(IdNcf(usuarioId, tipo));
        if (config == null || !config.Activo) throw new InvalidOperationException("Este tipo de comprobante no está registrado.");
        if (config.SiguienteNumero > config.NumeroFinal) throw new InvalidOperationException("Has llegado al final de la secuencia. Solicita una nueva a la DGII.");
        return $"{config.Prefijo}{config.SiguienteNumero:D8}";
    }

    private static string IdPeriodo(string usuarioId, int anio, int mes) => $"{usuarioId}|{anio:D4}{mes:D2}";

    private static void ValidarPeriodo(int anio, int mes)
    {
        if (anio is < 2000 or > 9998 || mes is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(mes), "El período fiscal no es válido.");
    }

    private static void AsegurarPeriodoAbierto(SQLiteConnection db, string usuarioId, DateTime fecha)
    {
        var id = IdPeriodo(usuarioId, fecha.ToLocalTime().Year, fecha.ToLocalTime().Month);
        var periodo = db.Find<PeriodoFiscal>(id);
        if (periodo?.Estado == "C")
            throw new InvalidOperationException($"El período {periodo.Mes:D2}/{periodo.Anio} está cerrado y no se permiten modificaciones.");
    }

    private static void QuitarIndiceUnicoNcf(SQLiteConnection db)
    {
        var indices = db.Query<IndiceSqlite>("SELECT name AS Name, sql AS Sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Factura' AND sql IS NOT NULL");
        foreach (var indice in indices.Where(indice => indice.Sql.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase)))
        {
            var nombreSeguro = indice.Name.Replace("'", "''", StringComparison.Ordinal);
            var columnas = db.Query<ColumnaIndiceSqlite>($"PRAGMA index_info('{nombreSeguro}')");
            if (columnas.Count == 1 && columnas[0].Name == "NCF")
                db.Execute($"DROP INDEX \"{indice.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
        }
    }

    private sealed class IndiceSqlite
    {
        public string Name { get; set; } = string.Empty;
        public string Sql { get; set; } = string.Empty;
    }

    private sealed class ColumnaIndiceSqlite
    {
        public string Name { get; set; } = string.Empty;
    }

    private class SecuenciaNCF
    {
        [PrimaryKey]
        public string Tipo { get; set; } = "B01";
        public int UltimoNumero { get; set; }
    }
}

public sealed class LicenciaLimiteException : Exception
{
    public bool EsCotizacion { get; }
    public int Usados { get; }
    public int FacturasEsteAnio { get; }
    public LicenciaLimiteException(bool esCotizacion, int usados, int facturasEsteAnio = 0) : base(esCotizacion
        ? $"Has usado {usados}/5 cotizaciones este mes. Activa PRO para más."
        : $"Límite del plan LITE alcanzado: {usados}/5 facturas en los últimos 30 días y {facturasEsteAnio}/40 este año.")
    {
        EsCotizacion = esCotizacion;
        Usados = usados;
        FacturasEsteAnio = facturasEsteAnio;
    }
}
