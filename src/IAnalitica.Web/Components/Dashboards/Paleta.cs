namespace IAnalitica.Web.Components.Dashboards;

/// <summary>Colores de las series (contraste ≥ 3:1 sobre blanco; series 0 y 1 = marca: naranja y berenjena). El color nunca es la única señal: hay etiquetas, valores y tabla de datos.</summary>
public static class Paleta
{
    public static readonly string[] Series = { "#D34615", "#772953", "#1F5FBF", "#0F766E", "#7C3AED", "#475569", "#15803D", "#A16207" };
    public static string De(int i) => Series[i % Series.Length];
    public const string Positivo = "#15803D";
    public const string Negativo = "#B91C1C";
    /// <summary>Barra de total o resultado (cascada): berenjena, distinto de positivo y negativo.</summary>
    public const string Total = "#772953";
}
