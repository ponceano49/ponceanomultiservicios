using System;
using SQLite;
using PonceanoFacturador.Models;

namespace PonceanoFacturador.Models
{
    public class DetalleFactura
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        public int FacturaId { get; set; }
        public int ArticuloId { get; set; }
        public int Cantidad { get; set; }
        public decimal PrecioUnitario { get; set; }
    }
}
