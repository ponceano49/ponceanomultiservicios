using SQLite;

namespace PonceanoFacturador.Models;

public class AjustesUsuario
{
    [PrimaryKey]
    public string UsuarioId { get; set; } = string.Empty;
    public string NombreCompania { get; set; } = string.Empty;
    public string RncCompania { get; set; } = string.Empty;
    public string CorreoFactura { get; set; } = string.Empty;
    public string Telefonos { get; set; } = string.Empty;
    public string Direccion1 { get; set; } = string.Empty;
    public string Direccion2 { get; set; } = string.Empty;
    public string LogoRuta { get; set; } = string.Empty;
    public string InstruccionPago { get; set; } = string.Empty;
    public string OpcionPago { get; set; } = "Efectivo";
    public int VenceEnDias { get; set; }
    public string Moneda { get; set; } = "Peso Dominicano";
    public string SimboloMoneda { get; set; } = "RD$";
    [Ignore]
    public decimal ImpuestoPorcentaje { get; set; } = 18m;
    [Column("ImpuestoPorcentaje")]
    public string ImpuestoPorcentajeAlmacenado
    {
        get => ImpuestoPorcentaje.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        set => ImpuestoPorcentaje = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
    public bool ImpuestoIncluido { get; set; }
    public string NotaPorDefecto { get; set; } = string.Empty;
    public string MensajeCorreo { get; set; } = string.Empty;
    public string NombreCampoCliente { get; set; } = "Cliente";
    public string NombreCampoRnc { get; set; } = "RNC/Cédula";
    public string ColorTema { get; set; } = "Azul";
    public string EnviarCopiaComo { get; set; } = "PDF";
    public string EnviarCopiaPor { get; set; } = "Correo";
    public bool NcfB01 { get; set; } = true;
    public bool NcfB02 { get; set; } = true;
    public bool NcfB03 { get; set; }
    public bool NcfB04 { get; set; } = true;
    public bool NcfB15 { get; set; }
    public string FormatoFecha { get; set; } = "dd/MM/yyyy";
    public string Idioma { get; set; } = "Español (República Dominicana)";
    public bool PagoEfectivo { get; set; } = true;
    public bool PagoTransferencia { get; set; } = true;
    public bool PagoCheque { get; set; } = true;
    public bool PagoTarjeta { get; set; } = true;
    public bool PagoCredito { get; set; }
    public bool PagoOtro { get; set; }
    public string MetodoOtro { get; set; } = string.Empty;
    public string BancoNombre { get; set; } = string.Empty;
    public string BancoNumeroCuenta { get; set; } = string.Empty;
    public string BancoTipoCuenta { get; set; } = "Ahorros";
    public string BancoRnc { get; set; } = string.Empty;
    public string MensajeReferencia { get; set; } = "Indique el número de factura como referencia";
    public bool RecargoActivo { get; set; }
    [Ignore]
    public decimal RecargoPorcentaje { get; set; }
    [Column("RecargoPorcentaje")]
    public string RecargoPorcentajeAlmacenado
    {
        get => RecargoPorcentaje.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        set => RecargoPorcentaje = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
    public string RecargoMotivo { get; set; } = string.Empty;
    public bool StripeActivo { get; set; }
    public bool PayPalActivo { get; set; }
    public string PayPalCorreo { get; set; } = string.Empty;
}
