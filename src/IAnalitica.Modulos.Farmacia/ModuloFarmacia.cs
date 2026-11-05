using System.Reflection;

namespace IAnalitica.Modulos.Farmacia;

/// <summary>Punto de entrada del módulo para la composición de los hosts (mediador, métricas y dashboards se registran desde aquí).</summary>
public static class ModuloFarmacia
{
    public static Assembly Ensamblado => typeof(ModuloFarmacia).Assembly;
}
