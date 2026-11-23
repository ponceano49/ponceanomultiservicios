using System.Globalization;

namespace PonceanoFacturador.Services;

public static class CorreccionFiscalMontos
{
    public static bool TryLeer(string? texto, out decimal monto)
    {
        monto = 0;
        if (string.IsNullOrWhiteSpace(texto)) return false;
        return decimal.TryParse(texto.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out monto) && monto >= 0 && decimal.Round(monto, 2) == monto;
    }

    public static bool TryCalcularTotal(string? gravado, string? itbis, string? exento,
        string? otrosImpuestos, string? propina, out decimal total)
    {
        total = 0;
        if (!TryLeer(gravado, out var baseGravada) || !TryLeer(itbis, out var impuesto) ||
            !TryLeer(exento, out var baseExenta) || !TryLeerOpcional(otrosImpuestos, out var otros) ||
            !TryLeerOpcional(propina, out var propinaLegal)) return false;
        try
        {
            total = checked(baseGravada + impuesto + baseExenta + otros + propinaLegal);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryLeerOpcional(string? texto, out decimal monto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            monto = 0;
            return true;
        }
        return TryLeer(texto, out monto);
    }
}
