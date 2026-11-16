# PL-04 · Metas de KPI y alertas

Estado: diseñado, no implementado. Script `1080_metas_alertas.sql`. Cierra el pendiente técnico 4 (trabajo `ALERTAS`, alerta `VENENO`) salvo el cierre de periodo (PL-05).
Preparación (2026-11-16): **listo para implementar.** DP-4 (D56: `admin.usuarios`) y DP-13 (D64: último mes completo) aprobadas. Destinatarios de alertas: administrador y GERENTE.
Revisado contra el código el 2026-11-16: se corrigieron cuatro huecos (página de avisos, disparo diario, alcance de sistema independiente del host y detección de `VENENO`). Ver «Cambios de la revisión» al final.

## Objetivo
Que la empresa fije metas y umbrales por KPI, vea el semáforo (ya implementado en lectura) y reciba un aviso cuando un KPI cruza su umbral o una carga falla.

## Situación actual (verificada)
- `cfg.KpiMeta (EmpresaId, KpiCodigo, PeriodoKey, Meta, UmbralAlerta)` existe; `MetasKpi.ObtenerAsync` la lee; `Semaforos.Calcular` pinta el KPI. **Nadie la escribe.**
- Métricas con `KpiMeta`: `MARGEN_OPERACIONAL`, `RAZON_CORRIENTE`, `ENDEUDAMIENTO`, `PCT_FE_PROBLEMA`, `DIAS_RADICACION`, `PCT_ANULADO`, `PCT_CARTERA_90`, `DSO` (+ las de PL-01/02/03).
- `seg.Notificacion.UsuarioId NULL` = «todos los administradores», pero `Notificaciones.ContarNoLeidasAsync` solo cuenta `UsuarioId = @usuarioId` y `INotificaciones.NotificarAsync` exige `int usuarioId`.
- **No existe una lista de avisos.** El contador de `MainLayout.razor` se pinta junto a «Mis reportes» y solo se apaga con `MarcarLeidasAsync("REPORTE")` al abrir `/reportes`. Un aviso de otro tipo subiría ese contador y nadie podría leerlo ni apagarlo.
- El motor de métricas (`MotorMetricas`) y `MetasKpi` solo usan `ISesionUsuario.EmpresaId` y `IFabricaConexiones` (`rol_app` + `IContextoBdAccesor`). `ISesionUsuario` se registra distinto en cada host: `SesionApi` (API), `SesionUsuarioHttp` (Web), `SesionTrabajo` (Worker, vía `AddGeneracionReportes`).
- **No hay nada que dispare trabajos por reloj** salvo `ProgramadorFacturacion` (cada 5 min pide al `PlanificadorFacturacion` que encole lo vencido).
- `ColaTrabajosSql.FallarAsync` devuelve `Task` (no dice si quedó en `VENENO`); además un trabajo puede pasar a `VENENO` en `TomarSiguienteAsync` por lease vencido, camino que `DespachadorTrabajos` no ve.

## Cambios
### 1. Pantalla `/metas`
- Application `Semantica/Metas.cs` (nuevo): `ListarMetasKpiQuery` (KPI configurables = métricas del catálogo con `KpiMeta`, de los módulos activos, con su meta vigente), `GuardarMetaKpiCommand(KpiCodigo, PeriodoKey, Meta, UmbralAlerta)` + validador (código existe en el catálogo; periodo `yyyymm` válido; umbral coherente con `SentidoMejora`: en `MenorEsMejor` umbral ≥ meta, en `MayorEsMejor` umbral ≤ meta), `EliminarMetaKpiCommand`.
- Permiso: `[RequierePermiso("admin.usuarios")]` (D56). `IComandoTransaccional`. Registro con `IAuditoriaAdministrativa.RegistrarAsync` (valor anterior y nuevo).
- **Prohibido recibir `EmpresaId`**: sale de la sesión.
- La meta rige desde su `PeriodoKey` hasta la siguiente (así ya la lee `MetasKpi`). Mostrarlo en la pantalla.
- Caché: los widgets se cachean 10 min por versión de carga. `ICacheConsultas` solo tiene `ObtenerOCrearAsync` (verificado: **no hay invalidación**). Solución: `IVersionConfiguracion.ObtenerAsync(empresaId)` (contador por empresa en `cfg.EmpresaParametro`, clave `CACHE.VERSION_CONFIG`, que el comando incrementa en la misma transacción; ausente = 0) y agregarlo a la clave `widget|…` de `ObtenerDatosWidgetHandler`, junto a la versión de carga. PL-08 reutiliza este contador.
- Web: `Components/Pages/Metas.razor` (SSR, formulario por KPI, patrón de `Costos.razor`), entrada en el menú.

### 2. Avisos del portal (lo usan también PL-05, PL-16 y PL-17)
- `INotificaciones` (`Application/Reportes/Contratos.cs`):
  - `NotificarAsync(int empresaId, int? usuarioId, …)`: `null` = aviso compartido de la empresa.
  - `ContarNoLeidasAsync(string? tipoPrefijo, CancellationToken)`: el contador de «Mis reportes» pasa `"REPORTE"`; el de «Avisos» cuenta todo lo demás.
  - `ListarAsync(int maximo, CancellationToken)`: los últimos avisos visibles para el usuario (tope 50), con su estado de lectura.
  - `MarcarLeidasAsync(string tipoPrefijo, …)` se conserva para los reportes; `MarcarAvisosLeidosAsync` marca lo listado.
- **Quién ve un aviso compartido** (`UsuarioId IS NULL`): usuarios con rol `ADMIN_EMPRESA` o `GERENTE`. `ISesionUsuario` no expone roles, así que `Notificaciones` lo resuelve en SQL con `EXISTS` sobre `seg.UsuarioRol ⨝ seg.Rol` (`Name IN ('ADMIN_EMPRESA','GERENTE')`), la misma tabla que ya lee `PermisosUsuario`. No se deriva de un permiso `dash.*`.
- Una fila compartida no puede tener un «leída» por usuario: tabla `seg.NotificacionLeida (EmpresaId, NotificacionId, UsuarioId, LeidaEn, PK (NotificacionId, UsuarioId))`. **Lleva `EmpresaId`** aunque sea derivable: `9000_rls.sql` solo aplica política a tablas con esa columna.
- Web: `Components/Pages/Avisos.razor` (`/avisos`: lista con tipo, título, fecha y enlace; al abrirla se marcan leídos) y, en `MainLayout.razor`, la entrada «Avisos (n)» separada de «Mis reportes (n)». Sin esto los avisos de este plan no se pueden leer.

### 3. Alcance de sistema por empresa (mecanismo compartido con PL-05, PL-15 y PL-16)
Evaluar métricas de una empresa sin un usuario detrás. Lo que existe no alcanza: `SesionTrabajo.Establecer(int usuarioId, …)` exige un usuario real y, en el host Web, cualquier alcance de DI resuelve `ISesionUsuario` a la sesión HTTP del usuario conectado. Por eso **no se resuelve `ISesionUsuario` del contenedor**; se construyen los servicios con una sesión explícita:
- `Infrastructure/Semantica/AlcanceSistema.cs` (todo `internal`):
  - `SesionSistema(int empresaId) : ISesionUsuario` inmutable: `UsuarioId = null`, `EmpresaId = empresaId`, `EsSuperAdmin = false`, `TienePermiso` siempre `false`.
  - `IFabricaAlcanceSistema.Crear(int empresaId)` → `AlcanceSistema : IAsyncDisposable` con `Motor` (`IMotorMetricas`), `Metas` (`IMetasKpi`), `Modulos` (`IModulosEmpresa`) y `Conexiones` (`IFabricaConexiones`).
  - Implementación: `IServiceScopeFactory.CreateAsyncScope()`; en ese alcance `IContextoBdAccesor.Establecer(ContextoBd.DeApi(empresaId))` (ya existe: empresa sin usuario; la RLS filtra por `EmpresaId`); `Motor` y `Metas` con `ActivatorUtilities.CreateInstance<MotorMetricas>(sp, sesion)` y `CreateInstance<MetasKpi>(sp, sesion)`; `Modulos` y `Conexiones` se resuelven del alcance (toman el contexto recién fijado).
- Conexión `rol_app`; **no** usar `rol_etl` para leer `rpt.*` (ese rol salta la RLS: `fn_FiltroEmpresa` lo exime).
- No pasa por `ISender` ni por los handlers de dashboards (rechazan `UsuarioId` nulo y exigen permisos). Como no hay permisos, quien lo usa comprueba `Modulos.EstaActivoAsync` del módulo de cada métrica antes de evaluarla.
- **Regla para las calculadoras:** `MotorMetricas` instancia cada `IMetricaCalculada` con el proveedor del alcance; una calculadora que inyecte `ISesionUsuario` recibiría la sesión del host, no la de sistema. Las calculadoras solo usan `IContextoCalculo` (`EmpresaId`, `ConsultarAsync`). Hoy no hay ninguna; agregar una prueba de arquitectura en `CatalogoAnaliticoTests` que falle si el constructor de una calculadora registrada pide `ISesionUsuario`.
- Funciona igual en Worker y en Web. Solo es alcanzable desde código de Infrastructure (sin endpoint ni comando que reciba un `EmpresaId`). Prueba propia: con el alcance de A no se lee nada de B.

### 4. Trabajo `ALERTAS`
- `TiposTrabajo.Alertas = "ALERTAS"`; `ManejadorAlertas` en Infrastructure (`AddProcesamiento`). Clave `EMP:{id}|ALERTAS`, prioridad 9; payload `{"encoladoEn":"<UTC ISO>"}`. La exclusión por clave evita duplicados.
- **Dos disparos:**
  1. Tras cargar `asientos`: `ProcesadorCargas.ProcesarAsync` lo encola junto a `CALCULAR_CARTERA`.
  2. **Diario**, aunque no llegue ninguna carga: `PlanificadorAlertas` (Infrastructure, con pruebas) + `ProgramadorAlertas` (`BackgroundService` del Worker, mismo patrón que `ProgramadorFacturacion`): cada 30 min, pasadas las 07:00 de Colombia, encola `ALERTAS` para cada empresa con al menos una carga `OK` de `asientos` que no tenga un trabajo `ALERTAS` creado ese día. Sin este disparo la «carga tardía» no se detectaría nunca: justo falta la carga que encolaría el trabajo.
- **Orden real de la cadena:** el worker corre varios ciclos y `Prioridad` solo ordena dentro del mismo tipo, así que encolar después de la cartera no garantiza que los costos ya terminaron. `ManejadorAlertas` devuelve `ResultadoTrabajo.Postergado` (5 min; `PostergarAsync` no gasta intento) mientras la empresa tenga trabajos `PENDIENTE` o `EN_PROCESO` de tipo `PROCESAR_CARGA`, `CALCULAR_CARTERA` o `CALCULAR_COSTOS`. Tope: si pasaron 2 h desde `encoladoEn` (`TrabajoTomado` no trae `CreadoEn`; por eso va en el payload) evalúa con lo que haya y lo anota en el log.
- Usa `IFabricaAlcanceSistema` (§3) para evaluar y `IFabricaConexionesEtl` solo para consultar `etl.Trabajo` y `etl.Carga`.
- Por cada meta vigente con `UmbralAlerta`: evaluar la métrica sin dimensiones → `Semaforos.Calcular`. Periodo evaluado (D64): **último mes completo** en hora Colombia (`PeriodoDesde = PeriodoHasta = mes anterior`); el mes en curso está a medias y daría falsos rojos. Métricas `Acumulado`: `PeriodoHasta` = ese mes. Métricas por `corte` (DSO, % > 90): último corte, y `PeriodoKey` del estado = mes del corte. Métricas sin `periodo` ni `corte` (p. ej. dinero detenido de PL-03): sin filtros, y `PeriodoKey` del estado = mes en curso.
- Tabla nueva `cfg.KpiAlertaEstado (EmpresaId, KpiCodigo, PeriodoKey, Semaforo, Valor, EvaluadoEn, PK (EmpresaId, KpiCodigo, PeriodoKey))`: solo se notifica **cuando el semáforo cambia a rojo** (o vuelve a verde), no cada noche. RLS automática (esquema `cfg`, tiene `EmpresaId`).
- Notificación: `seg.Notificacion (UsuarioId NULL, Tipo 'KPI_ALERTA', Titulo «DSO: 96 días, umbral 90», Enlace al dashboard del KPI)`. Sin datos de personas.

### 5. Fallas de proceso (dentro de `ALERTAS`, sin tocar `DespachadorTrabajos`)
- `CARGA_FALLIDA`: trabajos de la empresa en `VENENO` creados en las últimas 48 h → un aviso por trabajo («El proceso nocturno no terminó; soporte fue informado»), deduplicado guardando el `TrabajoId` en `Enlace` (`/avisos?t={id}`), + `log.LogCritical` con `TrabajoId` y tipo (sin payload). Consultarlo aquí cubre también el `VENENO` por lease vencido. Latencia máxima: un día (el disparo diario).
- `CARGA_TARDIA`: la última carga `OK` de `asientos` tiene más de 36 h → un aviso por día (deduplicar por tipo + fecha Colombia).
- El aviso al superadmin por correo queda fuera (P15, D32).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1080_metas_alertas.sql` (2 tablas: `cfg.KpiAlertaEstado`, `seg.NotificacionLeida`) |
| Application | `Semantica/Metas.cs` (nuevo), `Semantica/Consultas.cs` (clave de caché), `Abstracciones/ColaTrabajos.cs` (`TiposTrabajo.Alertas`), `Abstracciones/Sesion.cs` (`IVersionConfiguracion`), `Reportes/Contratos.cs` (`INotificaciones`) |
| Infrastructure | `Semantica/MetasKpiEscritura.cs` (nuevo), `Semantica/AlcanceSistema.cs` (nuevo), `Semantica/ManejadorAlertas.cs` (nuevo), `Semantica/PlanificadorAlertas.cs` (nuevo), `Reportes/ServiciosReportes.cs` (`Notificaciones`), `Reportes/GeneradorReportes.cs` (llamada a `NotificarAsync`), `Ingesta/Procesamiento.cs` (encolar), `Persistencia/ServiciosDeConsulta.cs` (`VersionConfiguracion`), `DependencyInjection.cs` |
| Worker | `ProgramadorAlertas.cs` (nuevo), `Program.cs` |
| Web | `Components/Pages/Metas.razor`, `Components/Pages/Avisos.razor`, `Components/Pages/Reportes.razor` (contador), `Components/Layout/MainLayout.razor`, endpoints `/q/metas` |
| Pruebas | `Application.Tests` (validador, handler), `Domain.Tests/CatalogoAnaliticoTests.cs` (calculadoras), `Integration.Tests/Ingesta/AlertasTests.cs`, `Web/MetasWebTests.cs`, `Web/AvisosWebTests.cs`, aislamiento |

Símbolos para `impact`: `MetasKpi`, `IMetasKpi`, `MotorMetricas`, `IContextoBdAccesor`, `ContextoBd`, `IVersionCarga`, `Notificaciones`, `INotificaciones`, `ProcesadorCargas.ProcesarAsync`, `TiposTrabajo`, `ObtenerDatosWidgetHandler`, `ManejadorGenerarReporte`.

## Pruebas
- Guardar, reemplazar y eliminar meta; validaciones; sin permiso → 403; auditoría escrita; el widget refleja la meta nueva sin esperar los 10 min de caché.
- `ALERTAS`: KPI cruza a rojo → 1 aviso; segunda corrida sin cambio → 0; vuelve a verde → 1.
- `ALERTAS` con un `CALCULAR_COSTOS` pendiente de la empresa → se posterga y no evalúa; con `encoladoEn` de hace más de 2 h evalúa; evalúa el último mes completo, no el mes en curso.
- Planificador: a las 06:00 no encola; a las 07:30 encola una vez por empresa; segunda pasada el mismo día no duplica; empresa sin cargas de `asientos` no entra.
- Alcance de sistema: evalúa métricas de la empresa pedida; no ve filas de otra; una métrica de un módulo inactivo no se evalúa; creado desde un alcance Web con un usuario de otra empresa en sesión, sigue leyendo la empresa pedida.
- `seg.NotificacionLeida` y `cfg.KpiAlertaEstado` aparecen en `seg.PolTenant` tras aplicar el script.
- Aviso compartido: lo ven el administrador y un `GERENTE`, no un usuario `CONSULTA`; marcar leído de un usuario no afecta a otro; el contador de «Mis reportes» no cuenta avisos.
- Trabajo en `VENENO` (por agotar intentos y por lease vencido) → un `CARGA_FALLIDA`, no dos en corridas sucesivas; sin datos de la carga.
- Sin carga de `asientos` en 36 h → `CARGA_TARDIA` una vez al día.
- Aislamiento: metas, estados y avisos de A invisibles para B.

## Riesgos y supuestos `[S]`
- El alcance de sistema es el punto delicado. Si al implementarlo el motor o `IMetasKpi` resultan depender de `UsuarioId` por otra vía, **parar y volver a diseño** (no abrir un atajo con `rol_etl`).
- Quién recibe alertas financieras: administrador y GERENTE. `dash.nomina_costos` es solo gerencia: no alertar KPI de nómina a otros (hoy coincide con los destinatarios).
- `INotificaciones` cambia de firma: revisar el `impact` completo (lo usan `ManejadorGenerarReporte`, `Reportes.razor` y `MainLayout.razor`).
- Costo del disparo diario: una evaluación por empresa y por meta con umbral; despreciable frente a la cadena nocturna.

## Cambios de la revisión (2026-11-16)
| Antes | Ahora | Motivo (verificado en el código) |
|---|---|---|
| Avisos «en la campana» | Página `/avisos` y contador propio | No hay lista de avisos; el contador vive en «Mis reportes» y solo se apaga para `REPORTE*` |
| `ALERTAS` solo tras `CALCULAR_CARTERA` | Además, disparo diario por reloj | La carga tardía no se puede detectar con un trabajo que nace de una carga |
| `SesionTrabajo.EstablecerSistema` | `IFabricaAlcanceSistema` con sesión explícita | En Web, `ISesionUsuario` del contenedor es siempre la sesión HTTP; PL-15 lo necesita ahí |
| Aviso de `VENENO` en `DespachadorTrabajos` | Consulta en `ALERTAS` | `FallarAsync` no informa el estado y el `VENENO` por lease vencido no pasa por el despachador; se evita tocar el núcleo del worker |
| Rol de los destinatarios sin definir cómo se sabe | `seg.UsuarioRol ⨝ seg.Rol` | `ISesionUsuario` no expone roles |
