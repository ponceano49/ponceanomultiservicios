using SQLite;

namespace PonceanoFacturador.Models;

public class UsuarioAutenticado
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Correo { get; set; } = string.Empty;
    [Indexed(Unique = true)]
    public string IdFirebase { get; set; } = string.Empty;
    public DateTime UltimoAcceso { get; set; }
}
