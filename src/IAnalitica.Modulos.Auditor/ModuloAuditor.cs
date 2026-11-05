using System.Reflection;
using IAnalitica.Domain.Auditoria;

namespace IAnalitica.Modulos.Auditor;

/// <summary>Punto de entrada del módulo para la composición de los hosts (mediador, métricas y dashboards se registran desde aquí).</summary>
public static class ModuloAuditor
{
    public static Assembly Ensamblado => typeof(ModuloAuditor).Assembly;

    /// <summary>Las reglas de la fase 1. Una regla nueva es una clase nueva en esta lista; el motor no cambia.</summary>
    public static IReadOnlyList<IReglaConfiguracion> Reglas() => new IReglaConfiguracion[]
    {
        new Con01(), new Con02(), new Con04(), new Fe01(), new Fe02(), new Fe03(), new Tar01(), new Tar02(), new Tar06(), new Usu01(),
    };
}
