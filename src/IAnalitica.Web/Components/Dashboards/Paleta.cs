namespace IAnalitica.Web.Components.Dashboards;

/// <summary>Colores de las series (contraste ≥ 3:1 sobre blanco). El color nunca es la única señal: hay etiquetas, valores y tabla de datos.</summary>
public static class Paleta
{
    public static readonly string[] Series = { "#1f5fbf", "#c2410c", "#0f766e", "#7c3aed", "#b45309", "#be185d", "#475569", "#15803d" };
    public static string De(int i) => Series[i % Series.Length];
    public const string Positivo = "#15803d";
    public const string Negativo = "#b91c1c";
}
