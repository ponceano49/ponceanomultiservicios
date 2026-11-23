using System.Globalization;

namespace PonceanoFacturador.Views;

internal sealed class ResumenGraficoDrawable : IDrawable
{
    public IReadOnlyList<(DateTime Mes, decimal Total)> Datos { get; set; } = Array.Empty<(DateTime, decimal)>();

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (Datos.Count == 0) return;
        var izquierda = 35f;
        var ancho = Math.Max(1f, dirtyRect.Width - izquierda - 15f);
        var alto = Math.Max(1f, dirtyRect.Height - 35f);
        canvas.StrokeColor = Color.FromArgb("#E5E5E5");
        canvas.StrokeSize = 1;
        for (var i = 0; i <= 5; i++)
        {
            var y = 5 + alto * i / 5;
            canvas.DrawLine(izquierda, y, izquierda + ancho, y);
        }
        var maximo = Datos.Max(dato => Math.Max(0m, dato.Total));
        var paso = Datos.Count > 1 ? ancho / (Datos.Count - 1) : 0f;
        var puntos = Datos.Select((dato, indice) => new PointF(izquierda + (Datos.Count == 1 ? ancho / 2 : indice * paso),
            5 + alto - (maximo > 0 ? (float)(Math.Max(0m, dato.Total) / maximo) * alto : 0))).ToArray();
        canvas.StrokeColor = Color.FromArgb("#93BBD0");
        canvas.FillColor = Color.FromArgb("#93BBD0");
        canvas.StrokeSize = 2;
        for (var i = 0; i < puntos.Length; i++)
        {
            if (i > 0) canvas.DrawLine(puntos[i - 1], puntos[i]);
            canvas.FillCircle(puntos[i].X, puntos[i].Y, 3.5f);
        }
        canvas.FontColor = Color.FromArgb("#777777");
        canvas.FontSize = 10;
        var intervalo = Math.Max(1, (int)Math.Ceiling(Datos.Count / Math.Max(1f, ancho / 70f)));
        for (var i = 0; i < Datos.Count; i += intervalo)
            canvas.DrawString(Datos[i].Mes.ToString("yyyy/M", CultureInfo.InvariantCulture), puntos[i].X - 30, alto + 10, 60, 20, HorizontalAlignment.Center, VerticalAlignment.Top);
    }
}
