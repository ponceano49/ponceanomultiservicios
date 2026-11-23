using SQLite;

namespace PonceanoFacturador.Services;

public sealed class CapturaFacturaDgii
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [Indexed]
    public string UsuarioId { get; set; } = string.Empty;
    public string Rnc { get; set; } = string.Empty;
    public string RazonSocial { get; set; } = string.Empty;
    public string Ncf { get; set; } = string.Empty;
    public string Subtotal { get; set; } = string.Empty;
    public string Itbis { get; set; } = string.Empty;
    public string Total { get; set; } = string.Empty;
    public string Moneda { get; set; } = "DOP";
    public string Archivo { get; set; } = string.Empty;
    public DateTime FechaRegistro { get; set; } = DateTime.UtcNow;
    [Ignore]
    public string Resumen => $"{Ncf} | {RazonSocial} | {(Moneda == "USD" ? "US$" : "RD$")} {Total}";
}

public sealed class NcfDuplicadoException : InvalidOperationException
{
    public NcfDuplicadoException(string ncf) : base($"Esta factura (NCF: {ncf}) ya está guardada en el sistema.") => Ncf = ncf;
    public string Ncf { get; }
}

public sealed class CapturaFacturasDgiiService
{
    private readonly SQLiteAsyncConnection _db;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public CapturaFacturasDgiiService() : this(Path.Combine(FileSystem.AppDataDirectory, "CapturasDgii.db3")) { }
    public CapturaFacturasDgiiService(string ruta) => _db = new SQLiteAsyncConnection(ruta);

    private async Task InicializarAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_initialized) return;
            await _db.CreateTableAsync<CapturaFacturaDgii>();
            var columnas = await _db.GetTableInfoAsync(nameof(CapturaFacturaDgii));
            if (!columnas.Any(columna => columna.Name == nameof(CapturaFacturaDgii.Moneda)))
                await _db.ExecuteAsync($"ALTER TABLE {nameof(CapturaFacturaDgii)} ADD COLUMN {nameof(CapturaFacturaDgii.Moneda)} TEXT NOT NULL DEFAULT 'DOP'");
            _initialized = true;
        }
        finally { _lock.Release(); }
    }

    public async Task<List<CapturaFacturaDgii>> ListarAsync(string usuarioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usuarioId);
        await InicializarAsync();
        return await _db.Table<CapturaFacturaDgii>().Where(f => f.UsuarioId == usuarioId).OrderByDescending(f => f.FechaRegistro).ToListAsync();
    }

    public async Task<bool> ExisteNcfAsync(string usuarioId, string ncf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usuarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ncf);
        await InicializarAsync();
        return await _db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CapturaFacturaDgii WHERE UsuarioId = ? AND UPPER(TRIM(Ncf)) = ?", usuarioId, ncf.Trim().ToUpperInvariant()) > 0;
    }

    public async Task GuardarAsync(CapturaFacturaDgii factura)
    {
        ArgumentNullException.ThrowIfNull(factura);
        ArgumentException.ThrowIfNullOrWhiteSpace(factura.UsuarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(factura.Ncf);
        factura.Ncf = factura.Ncf.Trim().ToUpperInvariant();
        await InicializarAsync();
        await _db.RunInTransactionAsync(db =>
        {
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM CapturaFacturaDgii WHERE UsuarioId = ? AND UPPER(TRIM(Ncf)) = ?", factura.UsuarioId, factura.Ncf) > 0)
                throw new NcfDuplicadoException(factura.Ncf);
            db.Insert(factura);
        });
    }

    public async Task ActualizarAsync(CapturaFacturaDgii factura)
    {
        ArgumentNullException.ThrowIfNull(factura);
        ArgumentException.ThrowIfNullOrWhiteSpace(factura.UsuarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(factura.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(factura.Ncf);
        factura.Ncf = factura.Ncf.Trim().ToUpperInvariant();
        await InicializarAsync();
        await _db.RunInTransactionAsync(db =>
        {
            var actual = db.Find<CapturaFacturaDgii>(factura.Id);
            if (actual is null || actual.UsuarioId != factura.UsuarioId)
                throw new InvalidOperationException("La captura ya no existe o no pertenece a esta cuenta.");
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM CapturaFacturaDgii WHERE UsuarioId = ? AND Id <> ? AND UPPER(TRIM(Ncf)) = ?", factura.UsuarioId, factura.Id, factura.Ncf) > 0)
                throw new NcfDuplicadoException(factura.Ncf);
            db.Update(factura);
        });
    }
}

public sealed record CapturaFacturaRevision(CapturaFacturaDgii Factura, string Detalle, string TextoOcr = "", bool DatosCliente = false)
{
    public string Resumen => string.IsNullOrWhiteSpace(Factura.Ncf) ? $"No se pudo leer NCF | {Factura.Archivo}" : $"{Factura.Ncf} | {Factura.RazonSocial} | {Factura.Moneda} {Factura.Total}";
}
