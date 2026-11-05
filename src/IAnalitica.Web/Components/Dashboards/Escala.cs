namespace IAnalitica.Web.Components.Dashboards;

/// <summary>Escala «bonita» para los ejes: pasos de 1, 2 o 5 × 10^k, con el cero incluido cuando corresponde.</summary>
public static class Escala
{
    public static (decimal Min, decimal Max, IReadOnlyList<decimal> Ticks) Bonita(decimal min, decimal max, int objetivo = 4)
    {
        if (max <= min) max = min + 1;
        var bruto = (double)(max - min) / objetivo;
        var magnitud = Math.Pow(10, Math.Floor(Math.Log10(bruto)));
        var f = bruto / magnitud;
        var paso = (decimal)((f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * magnitud);
        var inicio = Math.Floor(min / paso) * paso;
        var fin = Math.Ceiling(max / paso) * paso;
        var ticks = new List<decimal>();
        for (var v = inicio; v <= fin + paso / 100; v += paso) ticks.Add(v);
        return (inicio, fin, ticks);
    }

    /// <summary>Posición del texto del eje X: la primera etiqueta nace en el borde izquierdo y la última termina en el derecho (no se recortan).</summary>
    public static string Ancla(int i, int total) => total <= 1 ? "middle" : i == 0 ? "start" : i == total - 1 ? "end" : "middle";
}
