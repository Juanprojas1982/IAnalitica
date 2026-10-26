using System.Reflection;

namespace IAnalitica.Modulos.Auditor;

/// <summary>Punto de entrada del módulo para la composición de los hosts (mediador, métricas y dashboards se registran desde aquí).</summary>
public static class ModuloAuditor
{
    public static Assembly Ensamblado => typeof(ModuloAuditor).Assembly;
}
