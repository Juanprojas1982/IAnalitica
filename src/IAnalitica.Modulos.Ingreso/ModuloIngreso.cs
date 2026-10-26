using System.Reflection;

namespace IAnalitica.Modulos.Ingreso;

/// <summary>Punto de entrada del módulo para la composición de los hosts (mediador, métricas y dashboards se registran desde aquí).</summary>
public static class ModuloIngreso
{
    public static Assembly Ensamblado => typeof(ModuloIngreso).Assembly;
}
