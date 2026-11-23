using SQLite;

namespace PonceanoFacturador.Models;

public sealed class NcfUsuario
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    [Indexed]
    public string UsuarioId { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
    public bool Activo { get; set; }
    public string Prefijo { get; set; } = string.Empty;
    public int SiguienteNumero { get; set; } = 1;
    public int NumeroFinal { get; set; } = 99_999_999;
}
