using PonceanoFacturador.Models;
using PonceanoFacturador.Services;
using SQLite;

var path = Path.Combine(Path.GetTempPath(), $"ponceano-licencia-test-{Guid.NewGuid():N}.db3");
try
{
    var db = new DatabaseService(path);
    const string user = "test-user";
    const string other = "other-user";
    for (var i = 0; i < 5; i++) await db.IncrementarContadorDocumentoAsync(user, true);
    await Bloqueada(db, user);
    var raw = new SQLiteAsyncConnection(path);
    for (var i = 0; i < 5; i++)
        await raw.InsertAsync(new Factura { Id = Guid.NewGuid(), UsuarioId = user, NCF = $"B01{i:D8}", Fecha = DateTime.UtcNow, Estado = "Emitida" });
    Check(!await db.PuedeCrearFacturaAsync(user), "límite local de facturas");
    var expiry = DateTime.UtcNow.AddDays(10);
    db.EstablecerLicenciaRemota(user, expiry, true, () => true);
    await db.IncrementarContadorDocumentoAsync(user, true);
    await db.IncrementarContadorDocumentoAsync(user, false);
    Check(await db.PuedeCrearFacturaAsync(user), "licencia remota permite facturas");
    var visible = await db.ObtenerConfiguracionUsuarioAsync(user);
    var local = await db.ObtenerConfiguracionLicenciaSinActualizarAsync(user);
    Check(visible.LicenciaTipo == "Remota" && local.LicenciaTipo == "Gratuito" && local.LicenciaVencimiento is null, "licencia remota no se persiste");
    for (var i = 0; i < 5; i++) await db.IncrementarContadorDocumentoAsync(other, true);
    await Bloqueada(db, other);
    db.LimpiarLicenciaRemota();
    await Bloqueada(db, user);
    Check(!await db.PuedeCrearFacturaAsync(user), "la cuota de facturas vuelve tras cerrar sesión");
    db.EstablecerLicenciaRemota(user, DateTime.UtcNow.AddSeconds(-1), true, () => true);
    await Bloqueada(db, user);
    db.EstablecerLicenciaRemota(user, expiry, true, () => false);
    await Bloqueada(db, user);
    var inicio = DateTime.UtcNow;
    await db.ActivarPruebaGratuitaAsync("trial-user", "device", inicio, inicio.AddDays(30));
    db.EstablecerLicenciaRemota("trial-user", expiry, false, () => true);
    var trial = await db.ObtenerConfiguracionUsuarioAsync("trial-user");
    var original = await db.ObtenerConfiguracionLicenciaSinActualizarAsync("trial-user");
    Check(trial.LicenciaTipo == LicenciaPruebaService.TipoPrueba && original.FechaInicioPruebaUtc == inicio && original.IdentificadorHardware == "device", "prueba local conserva prioridad y datos");
    db.EstablecerLicenciaRemota("trial-user", expiry, true, () => true);
    trial = await db.ObtenerConfiguracionUsuarioAsync("trial-user");
    original = await db.ObtenerConfiguracionLicenciaSinActualizarAsync("trial-user");
    Check(trial.LicenciaTipo == "Remota" && original.FechaInicioPruebaUtc == inicio && original.IdentificadorHardware == "device", "vista remota no sobrescribe prueba");
    Console.WriteLine("LicenciaSqliteChecks: pruebas superadas.");
}
finally
{
    SQLiteAsyncConnection.ResetPool();
    if (File.Exists(path)) File.Delete(path);
}
static async Task Bloqueada(DatabaseService db, string id)
{
    try { await db.IncrementarContadorDocumentoAsync(id, true); }
    catch (LicenciaLimiteException) { return; }
    throw new Exception("No se aplicó el límite local de cotizaciones.");
}
static void Check(bool valid, string name) { if (!valid) throw new Exception(name); }
