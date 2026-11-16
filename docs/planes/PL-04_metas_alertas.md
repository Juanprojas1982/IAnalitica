# PL-04 · Metas de KPI y alertas

Estado: diseñado, no implementado. Script `1080_metas_alertas.sql`. Cierra el pendiente técnico 4 (trabajo `ALERTAS`, alerta `VENENO`) salvo el cierre de periodo (PL-05).
Preparación (2026-11-16): **listo para implementar.** DP-4 (D56: `admin.usuarios`) y DP-13 (D64: último mes completo) aprobadas. Destinatarios de alertas: administrador y GERENTE.

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
- Permiso: `[RequierePermiso("admin.usuarios")]` (D56). `IComandoTransaccional`. Registro en `aud.CambioAdministrativo` (valor anterior y nuevo).
- **Prohibido recibir `EmpresaId`**: sale de la sesión.
- La meta rige desde su `PeriodoKey` hasta la siguiente (así ya la lee `MetasKpi`). Mostrarlo en la pantalla.
- Caché: los widgets se cachean 10 min por versión de carga. `ICacheConsultas` solo tiene `ObtenerOCrearAsync` (verificado: **no hay invalidación**). Solución: `IVersionConfiguracion.ObtenerAsync(empresaId)` (contador por empresa en `cfg.EmpresaParametro`, clave `CACHE.VERSION_CONFIG`, que el comando incrementa en la misma transacción) y agregarlo a la clave `widget|…` de `ObtenerDatosWidgetHandler`. PL-08 reutiliza este contador.
- Web: `Components/Pages/Metas.razor` (SSR, formulario por KPI, patrón de `Costos.razor`), entrada en el menú.

### 2. Trabajo `ALERTAS`
- `TiposTrabajo.Alertas = "ALERTAS"`; `ManejadorAlertas` en Infrastructure. Se encola al final de la cadena: tras `CALCULAR_CARTERA` (clave `EMP:{id}|ALERTAS`, prioridad 9; la exclusión por clave evita duplicados en la misma noche).
- **Orden real de la cadena:** el worker ejecuta un trabajo a la vez y `Prioridad` solo ordena dentro del mismo tipo, así que encolar después de la cartera no garantiza que los costos ya terminaron. `ManejadorAlertas` devuelve `ResultadoTrabajo.Postergado` (5 min; `PostergarAsync` no gasta intento) mientras la empresa tenga trabajos `PENDIENTE` o `EN_PROCESO` de tipo `PROCESAR_CARGA`, `CALCULAR_CARTERA` o `CALCULAR_COSTOS`. Tope: tras 2 h postergando evalúa con lo que haya y lo anota en el log.
- **Sesión de sistema** (mecanismo compartido con PL-15 y PL-16). Lo que existe hoy no alcanza: `SesionTrabajo.Establecer(int usuarioId, …)` exige un usuario real y `ObtenerDatosWidgetHandler`/`ObtenerDashboardQuery` rechazan `UsuarioId` nulo. Diseño:
  - `SesionTrabajo.EstablecerSistema(int empresaId)`: `UsuarioId = null`, `EmpresaId = empresaId`, sin permisos (`TienePermiso` siempre `false`), `EsSuperAdmin = false`.
  - Contexto de BD: `ContextoBd.DeApi(empresaId)` (ya existe: empresa sin usuario; la RLS filtra solo por `EmpresaId`). Conexión `rol_app`; **no** usar `rol_etl` para leer `rpt.*` (ese rol salta la RLS: `fn_FiltroEmpresa` lo exime).
  - El manejador **no** pasa por `ISender` ni por los handlers de dashboards: usa `IMotorMetricas`, `IMetasKpi` y `CatalogoAnalitico` directamente (el motor solo necesita `EmpresaId`), dentro de un alcance DI propio como hace `GeneradorReportes.GenerarAsync`.
  - Como no hay permisos, el manejador comprueba él mismo `IModulosEmpresa.EstaActivoAsync` del módulo de cada métrica antes de evaluarla.
  - Solo es utilizable desde manejadores del worker (método `internal`, sin endpoint que lo alcance). Prueba propia: con sesión de sistema de A no se lee nada de B.
- Por cada meta vigente con `UmbralAlerta`: evaluar la métrica sin dimensiones → `Semaforos.Calcular`. Periodo evaluado (D64): **último mes completo** en hora Colombia (`PeriodoDesde = PeriodoHasta = mes anterior`); el mes en curso está a medias y daría falsos rojos en márgenes y ejecuciones. Métricas `Acumulado`: `PeriodoHasta` = ese mes. Métricas sin dimensión `periodo` (cartera por `corte`: DSO, % > 90): último corte, y `PeriodoKey` del estado = mes del corte.
- Tabla nueva `cfg.KpiAlertaEstado (EmpresaId, KpiCodigo, PeriodoKey, Semaforo, Valor, EvaluadoEn, PK (EmpresaId, KpiCodigo, PeriodoKey))`: solo se notifica **cuando el semáforo cambia a rojo** (o vuelve a verde), no cada noche. La RLS la cubre `9000_rls.sql` (esquema `cfg`, tiene `EmpresaId`).
- Notificación: `seg.Notificacion (UsuarioId NULL, Tipo 'KPI_ALERTA', Titulo «DSO: 96 días, umbral 90», Enlace al dashboard del KPI)`. Sin datos de personas.
- Corregir `Notificaciones` (`ServiciosReportes.cs`): contar, listar y marcar leídas también las de `UsuarioId IS NULL` **para usuarios con rol administrador o GERENTE**. Como una fila compartida no puede tener un «leída» por usuario: tabla `seg.NotificacionLeida (EmpresaId, NotificacionId, UsuarioId, LeidaEn, PK (NotificacionId, UsuarioId))` en el mismo script. **Lleva `EmpresaId`** aunque sea derivable: `9000_rls.sql` solo aplica política a tablas con esa columna; sin ella quedaría fuera de la RLS.

### 3. Fallas de proceso
- En `DespachadorTrabajos`, cuando `cola.FallarAsync` deja el trabajo en `VENENO` (y tiene `EmpresaId`): notificación `CARGA_FALLIDA` a la empresa («El proceso nocturno no terminó; soporte fue informado») + `log.LogCritical` con `TrabajoId` y tipo (sin payload).
- En `ALERTAS`: si la última carga `OK` de `asientos` tiene más de 36 h → notificación `CARGA_TARDIA` (una por día, deduplicar por título + fecha).
- El aviso al superadmin por correo queda fuera (P15, D32).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1080_metas_alertas.sql` (2 tablas) |
| Application | `Semantica/Metas.cs` (nuevo), `Abstracciones/ColaTrabajos.cs`, `Reportes/Contratos.cs` (`INotificaciones`) |
| Infrastructure | `Semantica/MetasKpiEscritura.cs` (nuevo), `Semantica/ManejadorAlertas.cs` (nuevo), `Reportes/ServiciosReportes.cs` (`Notificaciones`, `SesionTrabajo.EstablecerSistema`), `Ingesta/Procesamiento.cs`, `DependencyInjection.cs` |
| Web | `Components/Pages/Metas.razor`, menú en `MainLayout.razor`, endpoint `/q/metas` |
| Pruebas | `Application.Tests` (validador, handler), `Integration.Tests/Ingesta/AlertasTests.cs`, `Web/MetasWebTests.cs`, aislamiento |

Símbolos para `impact`: `MetasKpi`, `IMetasKpi`, `SesionTrabajo`, `ContextoBd`, `IVersionCarga`, `Notificaciones`, `INotificaciones`, `DespachadorTrabajos.EjecutarUnoAsync`, `ProcesadorCargas.ProcesarAsync`, `TiposTrabajo`, `ObtenerDatosWidgetHandler`.

## Pruebas
- Guardar, reemplazar y eliminar meta; validaciones; sin permiso → 403; auditoría escrita.
- `ALERTAS`: KPI cruza a rojo → 1 notificación; segunda corrida sin cambio → 0; vuelve a verde → 1.
- `ALERTAS` con un `CALCULAR_COSTOS` pendiente de la empresa → se posterga y no evalúa; evalúa el último mes completo, no el mes en curso.
- Sesión de sistema: evalúa métricas de la empresa del trabajo; no ve filas de otra empresa; una métrica de un módulo inactivo no se evalúa.
- `seg.NotificacionLeida` y `cfg.KpiAlertaEstado` aparecen en la política `seg.PolTenant` tras aplicar el script.
- Notificación compartida: la ve el administrador, no un usuario `CONSULTA`; marcar leída de un usuario no afecta a otro.
- `VENENO` genera `CARGA_FALLIDA` sin datos de la carga.
- Aislamiento: metas, estados y notificaciones de A invisibles para B.

## Riesgos y supuestos `[S]`
- La sesión de sistema en el worker es el punto delicado: queda definida en §2. Si al implementarla el motor o `IMetasKpi` resultan depender de `UsuarioId` por otra vía, **parar y volver a diseño** (no abrir un atajo con `rol_etl`).
- Quién recibe alertas financieras: administrador y GERENTE. `dash.nomina_costos` es solo gerencia: no alertar KPI de nómina a otros.
- `DespachadorTrabajos` es el corazón del worker: riesgo alto en `impact` casi seguro; cambio mínimo y con prueba de regresión de `ColaTrabajosTests`.
