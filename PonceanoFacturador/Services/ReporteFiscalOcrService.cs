using System.Text.Json;

namespace PonceanoFacturador.Services;

public sealed record RegistroReporteFiscalOcr(string Id, string Ncf, string Rnc, string Json, bool ClasificacionSupuesta = false);
public sealed record FilaReporteFiscalOcr(string FacturaId, BorradorFiscal606607 Datos, string[] Supuestos, string[] Pendientes);
public sealed record ReporteFiscalOcr(string Tipo, string RncCompania, string Advertencia, IReadOnlyList<FilaReporteFiscalOcr> Registros, IReadOnlyList<string> Excluidos);

public static class ReporteFiscalOcrService
{
    public static ReporteFiscalOcr Crear(string tipo, string rncCompania, IEnumerable<RegistroReporteFiscalOcr> facturas)
    {
        if (tipo is not ("606" or "607")) throw new ArgumentException("El reporte debe ser 606 o 607.", nameof(tipo));
        var filas = new List<FilaReporteFiscalOcr>();
        var excluidos = new List<string>();
        foreach (var factura in facturas)
        {
            if (string.IsNullOrWhiteSpace(factura.Json))
            {
                excluidos.Add($"{factura.Ncf}: sin JSON fiscal asociado");
                continue;
            }
            BorradorFiscal606607? datos;
            try
            {
                using var origen = JsonDocument.Parse(factura.Json);
                BorradorFiscal606607OllamaService.ValidarClaves(origen.RootElement);
                datos = JsonSerializer.Deserialize<BorradorFiscal606607>(factura.Json);
            }
            catch (JsonException ex) { throw new InvalidDataException($"El JSON fiscal de {factura.Ncf} no es válido.", ex); }
            if (datos is null || !string.Equals(datos.Ncf, factura.Ncf, StringComparison.OrdinalIgnoreCase) || datos.RncCedula != factura.Rnc)
                throw new InvalidDataException($"El JSON fiscal de {factura.Ncf} no corresponde a la factura registrada.");
            var supuestos = new List<string>();
            if (factura.ClasificacionSupuesta) supuestos.Add("tipo_operacion_primer_rnc");
            if (datos.TipoGastoOIngreso is null)
            {
                datos = datos with { TipoGastoOIngreso = tipo == "606" ? "Gastos Generales" : "Ventas de Bienes y Servicios" };
                supuestos.Add("tipo_gasto_o_ingreso");
            }
            if (datos.FormaPago is null)
            {
                datos = datos with { FormaPago = "Efectivo" };
                supuestos.Add("forma_pago");
            }
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(datos));
            var pendientes = json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Null).Select(p => p.Name).ToArray();
            filas.Add(new FilaReporteFiscalOcr(factura.Id, datos, supuestos.ToArray(), pendientes));
        }
        return new ReporteFiscalOcr(tipo, rncCompania, "Borrador interno; no es un archivo oficial DGII. Revisa los supuestos y datos pendientes.", filas, excluidos);
    }
}
