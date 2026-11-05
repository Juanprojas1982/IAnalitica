using System.Reflection;

namespace IAnalitica.Modulos.Ese;

/// <summary>Punto de entrada del módulo para la composición de los hosts (mediador, métricas y dashboards se registran desde aquí).</summary>
public static class ModuloEse
{
    public static Assembly Ensamblado => typeof(ModuloEse).Assembly;
}
