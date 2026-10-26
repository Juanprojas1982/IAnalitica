using System.Reflection;

namespace IAnalitica.Modulos.Financiero;

/// <summary>Punto de entrada del módulo para la composición de los hosts (mediador, métricas y dashboards se registran desde aquí).</summary>
public static class ModuloFinanciero
{
    public static Assembly Ensamblado => typeof(ModuloFinanciero).Assembly;
}
