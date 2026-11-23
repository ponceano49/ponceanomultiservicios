using System;
using SQLite;
using PonceanoFacturador.Models;

namespace PonceanoFacturador.Models
{
    public class Articulo
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        public string Codigo { get; set; } = string.Empty;
        public string Descripcion { get; set; } = string.Empty;
        public string TipoBienServicio { get; set; } = "Pendiente";
        [Ignore]
        public string ResumenInventario => $"Stock: {Stock} · Mín.: {StockMinimo} · Costo: RD$ {UltimoCosto:N2} · {TipoBienServicio}";
        [Ignore]
        public decimal Precio { get; set; }

        [Column("Precio")]
        public string PrecioAlmacenado
        {
            get => Precio.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            set => Precio = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public int Stock { get; set; }
        public int StockMinimo { get; set; }

        [Ignore]
        public decimal UltimoCosto { get; set; }

        [Column("UltimoCosto")]
        public string UltimoCostoAlmacenado
        {
            get => UltimoCosto.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            set => UltimoCosto = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        [Ignore]
        public decimal Itbis { get; set; } = 18m;

        [Column("Itbis")]
        public string ItbisAlmacenado
        {
            get => Itbis.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            set => Itbis = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
