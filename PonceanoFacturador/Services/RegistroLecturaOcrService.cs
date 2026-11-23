using System.Globalization;
using System.Text;

namespace PonceanoFacturador.Services;

public sealed record DatosLecturaOcr(
    string? RncEmisor,
    string? NombreEmisor,
    string? Ncf,
    DateTime? FechaEmision,
    string? RncComprador,
    decimal? Subtotal,
    decimal? Itbis,
    decimal? Total,
    IReadOnlyDictionary<string, string>? Metodos = null,
    IReadOnlyList<string>? ExplicacionProceso = null,
    string? NombreCliente = null);

public sealed class RegistroOcrAlmacenamientoException(string message, Exception innerException) : IOException(message, innerException);

public sealed class RegistroLecturaOcr
{
    internal RegistroLecturaOcr(string rutaDocumento, string rutaRegistro, string nombreOriginal, string rutaOriginal, string textoOriginal, string textoEnviadoOllama, string respuestaOllama, bool esError)
    {
        RutaDocumento = rutaDocumento;
        RutaRegistro = rutaRegistro;
        NombreOriginal = nombreOriginal;
        RutaOriginal = rutaOriginal;
        TextoOriginal = textoOriginal;
        TextoEnviadoOllama = textoEnviadoOllama;
        RespuestaOllama = respuestaOllama;
        EsError = esError;
    }

    internal string RutaDocumento { get; set; }
    internal string RutaRegistro { get; set; }
    internal bool EsError { get; set; }
    internal string NombreOriginal { get; }
    internal string RutaOriginal { get; }
    internal string TextoOriginal { get; }
    internal string TextoEnviadoOllama { get; }
    internal string RespuestaOllama { get; }
}

public sealed class RegistroLecturaOcrService
{
    private readonly string _raiz;

    public RegistroLecturaOcrService(string? raiz = null)
    {
        _raiz = Path.GetFullPath(raiz ?? Path.Combine(AppContext.BaseDirectory, "Reportes", "OCR"));
    }

    public string RutaRaiz => _raiz;

    public static bool LecturaCompleta(DatosLecturaOcr? datos) => datos is not null &&
        !string.IsNullOrWhiteSpace(datos.RncEmisor) && !string.IsNullOrWhiteSpace(datos.NombreEmisor) &&
        !string.IsNullOrWhiteSpace(datos.NombreCliente) && !string.IsNullOrWhiteSpace(datos.RncComprador) &&
        !string.IsNullOrWhiteSpace(datos.Ncf) && datos.FechaEmision.HasValue &&
        datos.Subtotal.HasValue && datos.Itbis.HasValue && datos.Total.HasValue;

    public void AsegurarCarpetas()
    {
        Directory.CreateDirectory(ObtenerCarpeta("Procesada"));
        Directory.CreateDirectory(ObtenerCarpeta("PorProcesar"));
        Directory.CreateDirectory(ObtenerCarpeta("Errores"));
    }

    public async Task<RegistroLecturaOcr> CrearAsync(
        string? rutaOriginal,
        string texto,
        DatosLecturaOcr? datos,
        bool error,
        string observaciones,
        CancellationToken cancellationToken = default,
        string? textoEnviadoOllama = null,
        string? respuestaOllama = null,
        string? nombreOriginal = null,
        bool requiereRevision = false)
    {
        var nombre = string.IsNullOrWhiteSpace(nombreOriginal)
            ? string.IsNullOrWhiteSpace(rutaOriginal) ? "Archivo no disponible" : Path.GetFileName(rutaOriginal)
            : Path.GetFileName(nombreOriginal);
        var carpeta = ObtenerCarpeta(error ? "Errores" : !requiereRevision && LecturaCompleta(datos) ? "Procesada" : "PorProcesar");
        var rutaRegistro = ObtenerRutaUnica(carpeta, CrearNombreRegistro(nombre));
        try
        {
            AsegurarCarpetas();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RegistroOcrAlmacenamientoException($"No se pudo crear el registro en '{rutaRegistro}': {ex.Message}", ex);
        }

        var registro = new RegistroLecturaOcr("", rutaRegistro, nombre, rutaOriginal ?? "", texto,
            textoEnviadoOllama ?? "No se envió ninguna solicitud a Ollama.", respuestaOllama ?? "Ollama no devolvió respuesta.", error);
        try
        {
            await EscribirRegistroAsync(registro, datos, observaciones, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RegistroOcrAlmacenamientoException($"No se pudo escribir el registro en '{rutaRegistro}': {ex.Message}", ex);
        }

        if (!string.IsNullOrWhiteSpace(rutaOriginal))
        {
            try
            {
                var origen = Path.GetFullPath(rutaOriginal);
                var destino = Path.Combine(carpeta, Path.GetFileName(origen));
                if (!File.Exists(origen)) throw new FileNotFoundException("No se encontró la factura original para moverla.", origen);
                if (string.Equals(origen, destino, StringComparison.OrdinalIgnoreCase))
                    registro.RutaDocumento = origen;
                else
                {
                    destino = ObtenerRutaUnica(carpeta, Path.GetFileName(origen));
                    File.Move(origen, destino);
                    registro.RutaDocumento = destino;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RegistroOcrAlmacenamientoException($"Registro guardado en '{rutaRegistro}', pero no se pudo mover la factura '{rutaOriginal}' a '{carpeta}': {ex.Message}", ex);
            }
        }
        return registro;
    }

    public async Task ActualizarAsync(
        RegistroLecturaOcr registro,
        DatosLecturaOcr datos,
        bool confirmada,
        string observaciones,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registro);
        ArgumentNullException.ThrowIfNull(datos);
        var carpeta = ObtenerCarpeta(confirmada && LecturaCompleta(datos) ? "Procesada" : "PorProcesar");
        Directory.CreateDirectory(carpeta);
        var rutaAnteriorDocumento = registro.RutaDocumento;
        var rutaAnteriorRegistro = registro.RutaRegistro;
        var errorAnterior = registro.EsError;
        var mismaCarpeta = string.Equals(Path.GetDirectoryName(rutaAnteriorRegistro), carpeta, StringComparison.OrdinalIgnoreCase);
        var nuevoDocumento = mismaCarpeta || string.IsNullOrWhiteSpace(rutaAnteriorDocumento)
            ? rutaAnteriorDocumento : ObtenerRutaUnica(carpeta, Path.GetFileName(rutaAnteriorDocumento));
        var nuevoRegistro = mismaCarpeta ? rutaAnteriorRegistro : ObtenerRutaUnica(carpeta, Path.GetFileName(rutaAnteriorRegistro));

        if (!mismaCarpeta && !string.IsNullOrWhiteSpace(rutaAnteriorDocumento)) File.Move(rutaAnteriorDocumento, nuevoDocumento);
        registro.RutaDocumento = nuevoDocumento;
        registro.RutaRegistro = nuevoRegistro;
        registro.EsError = false;
        var temporal = mismaCarpeta ? nuevoRegistro + "." + Guid.NewGuid().ToString("N") + ".tmp" : null;
        try
        {
            if (temporal is not null) registro.RutaRegistro = temporal;
            await EscribirRegistroAsync(registro, datos, observaciones, cancellationToken);
            if (temporal is not null)
            {
                File.Move(temporal, nuevoRegistro, true);
                registro.RutaRegistro = nuevoRegistro;
            }
            if (!mismaCarpeta && File.Exists(rutaAnteriorRegistro)) File.Delete(rutaAnteriorRegistro);
        }
        catch
        {
            if (temporal is not null && File.Exists(temporal)) File.Delete(temporal);
            if (!mismaCarpeta && File.Exists(nuevoRegistro)) File.Delete(nuevoRegistro);
            if (!mismaCarpeta && !string.IsNullOrWhiteSpace(nuevoDocumento) && File.Exists(nuevoDocumento)) File.Move(nuevoDocumento, rutaAnteriorDocumento);
            registro.RutaDocumento = rutaAnteriorDocumento;
            registro.RutaRegistro = rutaAnteriorRegistro;
            registro.EsError = errorAnterior;
            throw;
        }
    }

    private async Task EscribirRegistroAsync(RegistroLecturaOcr registro, DatosLecturaOcr? datos, string observaciones, CancellationToken cancellationToken)
    {
        var campos = new (string Nombre, string? Valor)[]
        {
            ("RNC Emisor", datos?.RncEmisor),
            ("Nombre Emisor", datos?.NombreEmisor),
            ("Nombre Cliente", datos?.NombreCliente),
            ("RNC Comprador", datos?.RncComprador),
            ("NCF", datos?.Ncf),
            ("Fecha Emisión", datos?.FechaEmision?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
            ("Subtotal", Formatear(datos?.Subtotal)),
            ("ITBIS", Formatear(datos?.Itbis)),
            ("Total", Formatear(datos?.Total))
        };
        var completos = datos is not null && campos.All(campo => !string.IsNullOrWhiteSpace(campo.Valor));
        var estado = registro.EsError ? "❌ Error"
            : completos ? "✅ Completo" : "⚠️ Incompleto";
        var contenido = new StringBuilder()
            .AppendLine("==================================================")
            .AppendLine("REGISTRO COMPLETO DE LECTURA")
            .AppendLine("==================================================")
            .AppendLine($"Fecha y Hora: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Archivo procesado: {registro.NombreOriginal}")
            .AppendLine($"Estado: {estado}")
            .AppendLine()
            .AppendLine("--- PASO 1 — TEXTO ENVIADO A OLLAMA ---")
            .AppendLine(registro.TextoEnviadoOllama)
            .AppendLine()
            .AppendLine("--- PASO 2 — RESPUESTA RECIBIDA DE OLLAMA ---")
            .AppendLine(registro.RespuestaOllama)
            .AppendLine()
            .AppendLine("--- PASO 3 — DATOS EXTRAÍDOS FINALES ---");

        foreach (var campo in campos)
        {
            var metodo = datos?.Metodos?.GetValueOrDefault(campo.Nombre);
            if (string.IsNullOrWhiteSpace(metodo))
                metodo = string.IsNullOrWhiteSpace(campo.Valor)
                    ? "No se encontró — dato pendiente de revisión manual."
                    : "No se registró el método específico; requiere revisión manual.";
            contenido.AppendLine($"{campo.Nombre}: {campo.Valor ?? ""}");
        }

        contenido.AppendLine()
            .AppendLine("--- PASO 4 — MÉTODO DE BÚSQUEDA UTILIZADO POR CAMPO ---");
        foreach (var campo in campos)
        {
            var metodo = datos?.Metodos?.GetValueOrDefault(campo.Nombre);
            if (string.IsNullOrWhiteSpace(metodo))
                metodo = string.IsNullOrWhiteSpace(campo.Valor)
                    ? "No se encontró — dato pendiente de revisión manual."
                    : "No se registró el método específico; requiere revisión manual.";
            contenido.AppendLine($"{campo.Nombre} → {metodo}");
        }

        var metodosRegistrados = datos?.Metodos ?? new Dictionary<string, string>();
        var leidos = campos.Where(campo => !string.IsNullOrWhiteSpace(campo.Valor) &&
            !metodosRegistrados.GetValueOrDefault(campo.Nombre, "").Contains("configurado", StringComparison.OrdinalIgnoreCase)).ToList();
        var calculados = campos.Where(campo => datos?.Metodos?.GetValueOrDefault(campo.Nombre, "")
            .Contains("calcul", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var inciertos = campos.Where(campo => string.IsNullOrWhiteSpace(campo.Valor) ||
            datos?.Metodos?.GetValueOrDefault(campo.Nombre, "").Contains("incierto", StringComparison.OrdinalIgnoreCase) == true).ToList();
        contenido.AppendLine()
            .AppendLine("--- EXPLICACIÓN DEL PROCESO ---")
            .AppendLine(leidos.Count > 0
                ? $"> Datos leídos/estructurados: {string.Join(", ", leidos.Select(campo => campo.Nombre))}."
                : "> No se pudieron confirmar datos leídos.")
            .AppendLine(calculados.Count > 0
                ? $"> Datos calculados: {string.Join(", ", calculados.Select(campo => campo.Nombre))}."
                : "> No se calcularon importes automáticamente.")
            .AppendLine(inciertos.Count > 0
                ? $"> Datos faltantes o inciertos: {string.Join(", ", inciertos.Select(campo => campo.Nombre))}."
                : "> No hay campos vacíos; la revisión humana sigue siendo necesaria.");
        if (datos?.Metodos is not null)
            foreach (var metodo in datos.Metodos.Where(item => item.Value.Contains("configurado", StringComparison.OrdinalIgnoreCase)))
                contenido.AppendLine($"> {metodo.Key}: {metodo.Value}.");
        var dificultades = datos?.ExplicacionProceso?.Where(item => !string.IsNullOrWhiteSpace(item)).ToList() ?? new List<string>();
        contenido.AppendLine(dificultades.Count > 0
            ? "> Dificultades: " + string.Join("; ", dificultades)
            : "> Dificultades: no se reportaron dificultades adicionales por el proceso.")
            .AppendLine()
            .AppendLine("--- PASO 5 — OBSERVACIONES ---")
            .AppendLine(observaciones)
            .AppendLine("==================================================")
            .ToString();
        await File.WriteAllTextAsync(registro.RutaRegistro, contenido.ToString(), new UTF8Encoding(false), cancellationToken);
    }

    private string ObtenerCarpeta(string nombre) => Path.Combine(_raiz, nombre);

    private static string CrearNombreRegistro(string nombreOriginal)
    {
        var nombre = Path.GetFileNameWithoutExtension(nombreOriginal);
        var invalido = Path.GetInvalidFileNameChars().ToHashSet();
        var limpio = new string(nombre.Select(caracter => invalido.Contains(caracter) ? '_' : caracter).ToArray()).Trim(' ', '.');
        if (limpio.Length > 80) limpio = limpio[..80];
        if (string.IsNullOrWhiteSpace(limpio)) limpio = "Factura";
        return $"RegistroOCR_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{limpio}.txt";
    }

    private static string ObtenerRutaUnica(string carpeta, string nombre)
    {
        var ruta = Path.Combine(carpeta, nombre);
        var indice = 2;
        while (File.Exists(ruta))
        {
            ruta = Path.Combine(carpeta, $"{Path.GetFileNameWithoutExtension(nombre)}_{indice}{Path.GetExtension(nombre)}");
            indice++;
        }
        return ruta;
    }

    private static string Formatear(decimal? valor) => valor?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
}
