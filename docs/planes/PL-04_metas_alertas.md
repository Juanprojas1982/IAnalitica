# PL-04 · Metas de KPI y alertas

Estado: diseñado, no implementado. Script `1080_metas_alertas.sql`. Cierra el pendiente técnico 4 (trabajo `ALERTAS`, alerta `VENENO`) salvo el cierre de periodo (PL-05).

## Objetivo
Que la empresa fije metas y umbrales por KPI, vea el semáforo (ya implementado en lectura) y reciba un aviso cuando un KPI cruza su umbral o una carga falla.

## Situación actual
- `cfg.KpiMeta (EmpresaId, KpiCodigo, PeriodoKey, Meta, UmbralAlerta)` existe; `MetasKpi.ObtenerAsync` la lee; `Semaforos.Calcular` pinta el KPI. **Nadie la escribe.**
- Métricas con `KpiMeta`: `MARGEN_OPERACIONAL`, `RAZON_CORRIENTE`, `ENDEUDAMIENTO`, `PCT_FE_PROBLEMA`, `DIAS_RADICACION`, `PCT_ANULADO`, `PCT_CARTERA_90`, `DSO` (+ las de PL-01/02/03).
- `seg.Notificacion.UsuarioId NULL` = «todos los administradores», pero `Notificaciones.ContarNoLeidasAsync` solo cuenta `UsuarioId = @usuarioId`.
- El motor de métricas necesita `ISesionUsuario` y conexión `rol_app`; el worker ya ejecuta reportes con el contexto guardado en `etl.Trabajo` (`GeneradorReportes`).

## Cambios
### 1. Pantalla `/metas`
- Application `Semantica/Metas.cs` (nuevo): `ListarMetasKpiQuery` (KPI configurables = métricas del catálogo con `KpiMeta`, de los módulos activos, con su meta vigente), `GuardarMetaKpiCommand(KpiCodigo, PeriodoKey, Meta, UmbralAlerta)` + validador (código existe en el catálogo; periodo `yyyymm` válido; umbral coherente con `SentidoMejora`: en `MenorEsMejor` umbral ≥ meta, en `MayorEsMejor` umbral ≤ meta), `EliminarMetaKpiCommand`.
- Permiso: `[RequierePermiso]` según DP-4. `IComandoTransaccional`. Registro en `aud.CambioAdministrativo` (valor anterior y nuevo).
- **Prohibido recibir `EmpresaId`**: sale de la sesión.
- La meta rige desde su `PeriodoKey` hasta la siguiente (así ya la lee `MetasKpi`). Mostrarlo en la pantalla.
- Caché: los widgets se cachean 10 min por versión de carga; tras guardar una meta, invalidar la caché de la empresa (revisar `ICacheConsultas`; si no tiene invalidación por empresa, incluir en la clave un contador de versión de metas).
- Web: `Components/Pages/Metas.razor` (SSR, formulario por KPI, patrón de `Costos.razor`), entrada en el menú.

### 2. Trabajo `ALERTAS`
- `TiposTrabajo.Alertas = "ALERTAS"`; `ManejadorAlertas` en Infrastructure. Se encola al final de la cadena: tras `CALCULAR_CARTERA` (clave `EMP:{id}|ALERTAS`, prioridad 9; la exclusión por clave evita duplicados en la misma noche).
- Contexto: ejecutar con sesión de sistema de la empresa (`EmpresaId` del trabajo, sin usuario, todos los módulos activos) y conexión `rol_app`. Reutilizar el mecanismo de alcance de `GeneradorReportes`; **no** usar `rol_etl` para leer vistas `rpt.*`.
- Por cada meta vigente con `UmbralAlerta`: evaluar la métrica en el periodo en curso (hora Colombia) sin dimensiones → `Semaforos.Calcular`.
- Tabla nueva `cfg.KpiAlertaEstado (EmpresaId, KpiCodigo, PeriodoKey, Semaforo, Valor, EvaluadoEn, PK (EmpresaId, KpiCodigo, PeriodoKey))`: solo se notifica **cuando el semáforo cambia a rojo** (o vuelve a verde), no cada noche. La RLS la cubre `9000_rls.sql` (esquema `cfg`, tiene `EmpresaId`).
- Notificación: `seg.Notificacion (UsuarioId NULL, Tipo 'KPI_ALERTA', Titulo «DSO: 96 días, umbral 90», Enlace al dashboard del KPI)`. Sin datos de personas.
- Corregir `Notificaciones` (`ServiciosReportes.cs`): contar, listar y marcar leídas también las de `UsuarioId IS NULL` **para usuarios con rol administrador o GERENTE** `[S]`. Como una fila compartida no puede tener un «leída» por usuario: tabla `seg.NotificacionLeida (NotificacionId, UsuarioId, LeidaEn)` en el mismo script.

### 3. Fallas de proceso
- En `DespachadorTrabajos`, cuando `cola.FallarAsync` deja el trabajo en `VENENO` (y tiene `EmpresaId`): notificación `CARGA_FALLIDA` a la empresa («El proceso nocturno no terminó; soporte fue informado») + `log.LogCritical` con `TrabajoId` y tipo (sin payload).
- En `ALERTAS`: si la última carga `OK` de `asientos` tiene más de 36 h → notificación `CARGA_TARDIA` (una por día, deduplicar por título + fecha).
- El aviso al superadmin por correo queda fuera (P15, D32).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1080_metas_alertas.sql` (2 tablas) |
| Application | `Semantica/Metas.cs` (nuevo), `Abstracciones/ColaTrabajos.cs`, `Reportes/Contratos.cs` (`INotificaciones`) |
| Infrastructure | `Semantica/MetasKpiEscritura.cs` (nuevo), `Semantica/ManejadorAlertas.cs` (nuevo), `Reportes/ServiciosReportes.cs`, `Ingesta/Procesamiento.cs`, `DependencyInjection.cs` |
| Web | `Components/Pages/Metas.razor`, menú en `MainLayout.razor`, endpoint `/q/metas` |
| Pruebas | `Application.Tests` (validador, handler), `Integration.Tests/Ingesta/AlertasTests.cs`, `Web/MetasWebTests.cs`, aislamiento |

Símbolos para `impact`: `MetasKpi`, `IMetasKpi`, `Notificaciones`, `INotificaciones`, `DespachadorTrabajos.EjecutarUnoAsync`, `ProcesadorCargas.ProcesarAsync`, `TiposTrabajo`, `ObtenerDatosWidgetHandler`.

## Pruebas
- Guardar, reemplazar y eliminar meta; validaciones; sin permiso → 403; auditoría escrita.
- `ALERTAS`: KPI cruza a rojo → 1 notificación; segunda corrida sin cambio → 0; vuelve a verde → 1.
- Notificación compartida: la ve el administrador, no un usuario `CONSULTA`; marcar leída de un usuario no afecta a otro.
- `VENENO` genera `CARGA_FALLIDA` sin datos de la carga.
- Aislamiento: metas, estados y notificaciones de A invisibles para B.

## Riesgos y supuestos `[S]`
- La sesión de sistema en el worker es el punto delicado: si no existe un mecanismo reutilizable, **parar y volver a diseño** (no abrir un atajo con `rol_etl`).
- Quién recibe alertas financieras (`[S]`: administrador y GERENTE). `dash.nomina_costos` es solo gerencia: no alertar KPI de nómina a otros.
- `DespachadorTrabajos` es el corazón del worker: riesgo alto en `impact` casi seguro; cambio mínimo y con prueba de regresión de `ColaTrabajosTests`.
