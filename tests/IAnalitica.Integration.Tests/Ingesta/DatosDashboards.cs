using IAnalitica.Application.Abstracciones;
using static IAnalitica.Integration.Tests.Ingesta.Procesamiento;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// Una empresa con contabilidad, facturación y cartera conocidas, para verificar los dashboards con cifras calculadas a mano.
/// <code>
/// Ingresos (clase 4)   ene 1.000 · feb 2.000 · oct-2025 800   (el cierre de feb por 3.000 NO cuenta)
/// Costos y gastos      ene 500 (gasto 300 + costo de ventas 200)
/// Balance a feb        bancos 5.200 · inventario 400 · deudores 3.300 → AC 8.900 · pasivo corriente 200
/// Cartera al 2026-03-01  CxC: 500 (31-60) + 2.000 (0-30) + 800 (151-180) = 3.300 · CxP 200
/// Facturas             F1 ene 1.000 (E1, FE enviada, RIPS 10 d) · F2 feb 2.000 (E2, FE con error) · F0 oct 800 (E2, cargada, RIPS 10 d) · F3 ene 500 anulada
/// </code>
/// </summary>
public static class DatosDashboards
{
    public const string Corte = "2026-03-01";

    public static async Task CargarAsync(Procesamiento p)
    {
        await p.ProcesarAsync("sedes", "COMPLETA", new object[] { new { codigoOrigen = "1", nombre = "Principal" } });
        await p.ProcesarAsync("usuarios-operativos", "COMPLETA", new object[] { new { codigoOrigen = "C:1", nombre = "Facturador Uno" } });
        await p.ProcesarAsync("tipos-comprobante", "COMPLETA", new object[]
        {
            new { codigoOrigen = "T1", prefijo = "CSI0", nombre = "Comprobante", claseSugerida = "NORMAL" },
            new { codigoOrigen = "T2", prefijo = "SIN0", nombre = "Saldo inicial", claseSugerida = "SALDO_INICIAL" },
            new { codigoOrigen = "T9", prefijo = "CIE0", nombre = "Cierre", claseSugerida = "CIERRE" },
        });
        await p.ProcesarAsync("cuentas", "COMPLETA", new object[]
        {
            new { codigo = "111005", nombre = "Bancos", nivel = 6, naturaleza = "D", esBanco = true },
            new { codigo = "130505", nombre = "Clientes", nivel = 6, naturaleza = "D", detalleCartera = 2 },
            new { codigo = "143505", nombre = "Inventario", nivel = 6, naturaleza = "D" },
            new { codigo = "220505", nombre = "Proveedores nacionales", nivel = 6, naturaleza = "C", detalleCartera = 1 },
            new { codigo = "310505", nombre = "Capital", nivel = 6, naturaleza = "C" },
            new { codigo = "360505", nombre = "Resultado del ejercicio", nivel = 6, naturaleza = "C" },
            new { codigo = "413501", nombre = "Servicios de salud", nivel = 6, naturaleza = "C" },
            new { codigo = "510506", nombre = "Sueldos", nivel = 6, naturaleza = "D" },
            new { codigo = "613501", nombre = "Costo de ventas", nivel = 6, naturaleza = "D" },
        });
        await p.ProcesarAsync("terceros", "INCREMENTAL", new object[]
        {
            new { codigoOrigen = "P1", identificacion = "900100100", esPaciente = false, nombre = "EPS Uno" },
            new { codigoOrigen = "P2", identificacion = "900200200", esPaciente = false, nombre = "EPS Dos" },
        });
        await p.ProcesarAsync("pagadores", "COMPLETA", new object[]
        {
            new { codigoOrigen = "E1", nombre = "EPS Uno", terceroCodigo = "P1", activo = true },
            new { codigoOrigen = "E2", nombre = "EPS Dos", terceroCodigo = "P2", activo = true },
        });

        await p.ProcesarAsync("asientos", "VENTANA", new[]
        {
            // saldo inicial (2026-01-01)
            Linea("S-1", "M0", "2026-01-01", "111005", 5000, 0, tipo: "T2"), Linea("S-2", "M0", "2026-01-01", "310505", 0, 5000, tipo: "T2"),
            Linea("S-3", "M0B", "2026-01-01", "143505", 400, 0, tipo: "T2"), Linea("S-4", "M0B", "2026-01-01", "310505", 0, 400, tipo: "T2"),
            // enero
            Linea("M1-CXC", "M1", "2026-01-10", "130505", 1000, 0, "P1", numDoc: "F1"), Linea("M1-ING", "M1", "2026-01-10", "413501", 0, 1000),
            Linea("M2-GAS", "M2", "2026-01-15", "510506", 300, 0), Linea("M2-BAN", "M2", "2026-01-15", "111005", 0, 300),
            Linea("M3-COS", "M3", "2026-01-20", "613501", 200, 0), Linea("M3-CXP", "M3", "2026-01-20", "220505", 0, 200, "P1", numDoc: "C1"),
            // febrero
            Linea("M4-CXC", "M4", "2026-02-05", "130505", 2000, 0, "P2", numDoc: "F2"), Linea("M4-ING", "M4", "2026-02-05", "413501", 0, 2000),
            Linea("M5-BAN", "M5", "2026-02-10", "111005", 500, 0), Linea("M5-ABO", "M5", "2026-02-10", "130505", 0, 500, "P1", rel: "M1-CXC"),
            Linea("M6-CIE", "M6", "2026-02-28", "413501", 3000, 0, tipo: "T9"), Linea("M6-RES", "M6", "2026-02-28", "360505", 0, 3000, tipo: "T9"),
            // factura vieja (octubre 2025)
            Linea("M7-CXC", "M7", "2025-10-01", "130505", 800, 0, "P2", numDoc: "F0"), Linea("M7-ING", "M7", "2025-10-01", "413501", 0, 800),
        }, new DateOnly(2025, 10, 1), new DateOnly(2026, 2, 28));

        await p.ProcesarAsync("facturas", "VENTANA", new[]
        {
            Factura("F1|1|1", "F1", "2026-01-10", "E1", 1000, estadoFe: 0, fechaRips: "2026-01-20", usuario: "C:1"),
            Factura("F2|1|1", "F2", "2026-02-05", "E2", 2000, estadoFe: 2, usuario: "C:1"),
            Factura("F0|1|1", "F0", "2025-10-01", "E2", 800, estadoFe: 1, fechaRips: "2025-10-11", usuario: "C:1"),
            Factura("F3|1|1", "F3", "2026-01-25", "E1", 500, anulada: true, usuario: "C:1"),
        }, new DateOnly(2025, 10, 1), new DateOnly(2026, 2, 28));
    }

    /// <summary>Calcula la cartera al corte (el trabajo de cartera ya está encolado por la carga de asientos).</summary>
    public static async Task CalcularCarteraAsync(Procesamiento p, string corte = Corte)
    {
        await p.EjecutarAsync("UPDATE etl.Trabajo SET Payload = @payload, DisponibleEn = SYSUTCDATETIME() WHERE ClaveExclusion = @clave AND Estado = 'PENDIENTE'",
            new { clave = $"EMP:{p.EmpresaId}|CARTERA", payload = $"{{\"corte\":\"{corte}\"}}" });
        while (await p.Despachador.EjecutarUnoAsync(TiposTrabajo.CalcularCartera, CancellationToken.None)) { }
    }
}
