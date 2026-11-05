using System.Reflection;

namespace IAnalitica.Modulos.Operacion;

/// <summary>Punto de entrada del módulo para la composición de los hosts (mediador, métricas y dashboards se registran desde aquí).</summary>
public static class ModuloOperacion
{
    public static Assembly Ensamblado => typeof(ModuloOperacion).Assembly;
}
