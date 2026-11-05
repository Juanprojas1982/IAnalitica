# Spec 14: Capa semántica, dashboards declarativos y funcionalidades analíticas

Estado: diseño (Opus). Implementación: Sonnet. Alcance: **solo sector salud** (24_SPEC_MAESTRA §1).
Reemplaza a `14_spec_arquitectura_extensible_multivertical.md` (eliminado: lo multivertical no aplica).

## 1. Objetivo
Crear **dashboards nuevos sin tocar el núcleo**:
- un dashboard que combina métricas existentes es **JSON en la BD**, sin código;
- una métrica nueva es un registro en su módulo;
- una métrica compleja es **una clase**;
- un tipo de gráfico nuevo es **un componente**, y es raro.

## 2. Conceptos
| Concepto | Definición | Dónde vive |
|---|---|---|
| **Módulo** | Grupo de funcionalidades de salud. Lista cerrada: FINANCIERO, CICLO_INGRESO, CARTERA, TESORERIA, GLOSAS, AGENDA, DIAGNOSTICO, HOSPITALARIO, FARMACIA, INVENTARIO, NOMINA_COSTOS, ESE_PRESUPUESTO, AUDITOR_CONFIG, IA | Proyecto `IAnalitica.Modulos.*`; activación por empresa en `seg.EmpresaModulo` |
| **Métrica** | Cálculo con nombre, unidad, sentido de mejora, fuente (vista `rpt.*`) y dimensiones permitidas | Código del módulo |
| **Dashboard** | Título, permiso, módulo y lista de widgets | `cfg.Dashboard` (JSON). Los de fábrica se cargan desde el código del módulo al desplegar |
| **Widget** | Tipo de gráfico + métricas + dimensiones + drill | Dentro del JSON del dashboard |

## 3. Contratos (Domain)
```csharp
// IAnalitica.Domain/Modulos/IModuloAnalitico.cs
namespace IAnalitica.Domain.Modulos;

public interface IModuloAnalitico
{
    string Codigo { get; }                                   // "CARTERA"
    string Nombre { get; }
    void Registrar(IRegistroModulo registro);
}

public interface IRegistroModulo
{
    void Metrica(DefinicionMetrica metrica);
    void DashboardDeFabrica(DefinicionDashboard dashboard);
    void HerramientaIA(DefinicionHerramientaIA herramienta);
}

public enum SentidoMejora { MayorEsMejor, MenorEsMejor, Neutro }
public enum TipoWidget { Kpi, Linea, Barras, BarrasApiladas, Cascada, Tabla, MapaCalor, Embudo, Dona }

public sealed record DefinicionMetrica(
    string Codigo,                                           // "cartera.saldo_vencido_mas_90"
    string Nombre,
    string Unidad,                                           // COP | % | dias | unidades
    SentidoMejora Sentido,
    string Modulo,
    string Fuente,                                           // vista rpt.* registrada en la lista blanca
    string Expresion,                                        // de un catálogo cerrado: SUM(col), COUNT(*), AVG(col), SUM(a)/NULLIF(SUM(b),0)
    IReadOnlyDictionary<string, string> Dimensiones,         // "pagador" -> "PagadorId", "periodo" -> "PeriodoKey"
    string? FiltroFijo = null,                               // p. ej. "RangoEdad IN ('91-120','121-150','151-180','181-360','>360')" (lista blanca)
    Type? Calculadora = null);                               // implementa IMetricaCalculada

public interface IMetricaCalculada
{
    Task<ResultadoMetrica> CalcularAsync(ConsultaMetrica consulta, CancellationToken ct);
}

public sealed record DefinicionDashboard(
    string Codigo, string Titulo, string Modulo, string Permiso, int Version,
    IReadOnlyList<DefinicionWidget> Widgets);

public sealed record DefinicionWidget(
    string Codigo, string Titulo, TipoWidget Tipo,
    IReadOnlyList<string> Metricas,
    IReadOnlyList<string> Dimensiones,
    int Fila, int Columna, int Ancho, int Alto,              // grilla de 12 columnas
    string? DrillDashboard = null,
    IReadOnlyList<string>? ModulosAdicionales = null);       // el widget se oculta si falta alguno
```
`ResultadoMetrica` y `ConsultaMetrica` (empresa de la sesión, filtros validados, periodo, comparativo) se definen en `Application`.

## 4. Motor de métricas (Infrastructure)
1. Recibe `ObtenerDatosWidgetQuery(dashboard, widget, filtros)`.
2. Verifica que el dashboard y sus módulos estén activos para la empresa y que el usuario tenga el permiso.
3. Para cada métrica declarativa arma **SQL parametrizado** con Dapper:
   - `SELECT dims, expr FROM fuente WHERE filtros GROUP BY dims`;
   - fuente, columnas, expresiones y filtros salen **solo** de la lista blanca registrada por los módulos;
   - **nunca** concatena texto del usuario.
4. Las métricas con `Calculadora` delegan en la clase.
5. **Caché:** clave `empresa + métrica + filtros + versión de carga`. Se invalida al terminar una carga de la empresa (spec 20 §4).
6. La conexión lleva `SESSION_CONTEXT` del usuario, así que la RLS aplica.

## 5. Renderizador (Blazor)
- `DashboardHost` lee la definición, ubica los widgets en una grilla de 12 columnas y resuelve **un componente por `TipoWidget`**.
- **Filtros globales** (periodo, sede, pagador, centro de costo): barra superior con estado en la URL. **Drill:** abre otro dashboard con los filtros heredados.
- **Estados de cada widget:** cargando (esqueleto), sin datos (mensaje: «este módulo no tiene datos aún» o «falta registrar X en su HIS»), error controlado.
- **Exportar** un widget o un dashboard a PDF o Excel pasa por los reportes en segundo plano (spec 21).

## 6. Cómo se crea un dashboard nuevo
| Caso | Qué se hace | ¿Toca el núcleo? |
|---|---|---|
| Con métricas existentes | **Fase 1:** JSON en el código del módulo (dashboard de fábrica). **Fase 2:** editor del superadmin que guarda en `cfg.Dashboard` | No |
| Métrica nueva sobre una vista existente | `registro.Metrica(...)` en el módulo | No |
| Métrica con lógica compleja | Clase `IMetricaCalculada` | No |
| Fuente nueva | Vista `rpt.*` nueva (script DbUp) + registro en la lista blanca del módulo | No |
| Tipo de gráfico nuevo | Componente Blazor + valor en `TipoWidget` | Sí (excepcional) |

## 7. Funcionalidades analíticas a incorporar por fase (de la comparación con el mercado)
| Funcionalidad | Fase |
|---|---|
| Metas, semáforos y alertas (`cfg.KpiMeta`) | 1 |
| Flujo de caja a 13 semanas | 1 |
| Calidad de datos (cuentas sin homologar, cargas tardías, descuadres) | 1 |
| Exportación a Excel y PDF (segundo plano) | 1 |
| Drill-down hasta el documento | 2 |
| Informe gerencial programado con comentarios | 2 |
| Comentarios sobre los datos | 2 |
| Consolidación de grupos de IPS con eliminaciones entre empresas | 2 |
| Homologación del plan de cuentas asistida por IA | 2 |
| Benchmarking anónimo entre instituciones | 3 |
| Pronóstico continuo y escenarios | 3 |
| Variación precio / volumen / mezcla (cascada) | 3 |

## 8. Riesgos
1. **Motor demasiado genérico:** se cubre el 80 % con expresiones declarativas y el resto con `IMetricaCalculada`. No se construye un lenguaje de fórmulas.
2. **Rendimiento:** vistas agregadas y caché por versión de carga. Los dashboards gerenciales no consultan los hechos línea por línea.
3. **Coherencia de cifras:** una métrica se define una sola vez y la consumen el dashboard, las alertas, los reportes y la IA.
