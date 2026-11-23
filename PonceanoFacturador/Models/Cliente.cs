using SQLite;

namespace PonceanoFacturador.Models;

public class Cliente
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string RncCedula { get; set; } = string.Empty;
    public string Direccion { get; set; } = string.Empty;
    public string Telefono { get; set; } = string.Empty;
    public string Correo { get; set; } = string.Empty;
    public bool EsContribuyente { get; set; }
    [Ignore]
    public string EstadoContribuyente => EsContribuyente ? "Contribuyente" : "Consumidor final";
    public DateTime FechaCreacion { get; set; }
}
