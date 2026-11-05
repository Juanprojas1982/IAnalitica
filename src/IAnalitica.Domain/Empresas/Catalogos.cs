namespace IAnalitica.Domain.Empresas;

/// <summary>Módulos activables por empresa: lista cerrada (23 §2, 24 §5.3).</summary>
public static class Modulos
{
    public const string Financiero = "FINANCIERO";
    public const string CicloIngreso = "CICLO_INGRESO";
    public const string Cartera = "CARTERA";
    public const string Tesoreria = "TESORERIA";
    public const string Glosas = "GLOSAS";
    public const string Agenda = "AGENDA";
    public const string Diagnostico = "DIAGNOSTICO";
    public const string Hospitalario = "HOSPITALARIO";
    public const string Farmacia = "FARMACIA";
    public const string Inventario = "INVENTARIO";
    public const string NominaCostos = "NOMINA_COSTOS";
    public const string EsePresupuesto = "ESE_PRESUPUESTO";
    public const string AuditorConfig = "AUDITOR_CONFIG";
    public const string Ia = "IA";

    public static readonly IReadOnlyList<string> Todos = new[]
    {
        Financiero, CicloIngreso, Cartera, Tesoreria, Glosas, Agenda, Diagnostico, Hospitalario,
        Farmacia, Inventario, NominaCostos, EsePresupuesto, AuditorConfig, Ia,
    };
}

public static class TiposEntidad
{
    public const string Ips = "IPS";
    public const string Ese = "ESE";
    public const string OperadorFarmaceutico = "OPERADOR_FARMACEUTICO";
    public const string Laboratorio = "LABORATORIO";
    public const string CentroDiagnostico = "CENTRO_DIAGNOSTICO";

    public static readonly IReadOnlyList<string> Todos = new[] { Ips, Ese, OperadorFarmaceutico, Laboratorio, CentroDiagnostico };
}

public static class Naturalezas
{
    public const string Privada = "PRIVADA";
    public const string Publica = "PUBLICA";
    public const string Mixta = "MIXTA";

    public static readonly IReadOnlyList<string> Todas = new[] { Privada, Publica, Mixta };
}

public static class TiposContacto
{
    public static readonly IReadOnlyList<string> Todos = new[]
        { "REPRESENTANTE", "GERENCIA", "FINANCIERO", "CONTADOR", "TI", "FACTURACION", "COMERCIAL" };
}
