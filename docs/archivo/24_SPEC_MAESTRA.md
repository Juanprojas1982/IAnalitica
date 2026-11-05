# IAnalitica — ESPECIFICACIÓN MAESTRA v1.0

Estado: **cerrada para implementación** (Opus, 2026‑10‑26; rev. 1.1 del 2026‑10‑26: DDL unificado y verificado en `database/scripts`, D35–D38). Implementa: Sonnet.
Repositorio: `Juanprojas1982/IAnalitica` (plataforma) y `Juanprojas1982/SaludIPS2` (conector, rama `feature/analitica-conector`).

## 0. Cómo usar este documento (obligatorio para quien implementa)
1. **Precedencia:**
   1. este documento;
   2. specs detalladas vigentes (§16);
   3. todo lo demás.

   El registro de decisiones (D01–D38) está en §17 de este documento.

   Si un documento contradice a uno de mayor precedencia, gana el de mayor precedencia. **Los documentos marcados «reemplazado» en §16 no se usan.**
2. **No inventar.** Si algo no está definido aquí ni en una spec vigente, **se pregunta** antes de implementarlo. No se asumen valores, nombres de tablas, reglas de negocio ni paquetes.
3. **Supuestos `[S]`.** Todo campo o regla marcado `[S]` se implementa **detrás de una bandera o con estado «por verificar»** y se lista en el PR. No se presenta como dato confirmado.
4. **Regla de costo (D32).** Antes de crear cualquier recurso de Azure, licencia o servicio pago: precio oficial con fecha + estimado mensual → **aprobación explícita del dueño**. En pruebas, solo niveles gratuitos o mínimos.
5. **Regla de modelos (D30).** El diseño se hace con Opus; el código, con Sonnet. Un cambio de diseño durante la implementación se devuelve a diseño.
6. **Idioma.** Dominio, base de datos, mensajes y documentación en español. Sufijos técnicos en inglés por convención .NET (`Handler`, `Repository`, `Options`, `Controller`).

## 1. Alcance del producto
**IAnalitica:** plataforma SaaS de analítica gerencial **solo para el sector salud** (IPS privadas, ESE públicas, operadores y dispensadores farmacéuticos). Recibe datos del HIS de cada institución (SaludIPS primero; otros HIS después, con el mismo contrato canónico) y entrega:
1. **Dashboards gerenciales por módulo:** financiero, ciclo del ingreso, cartera, tesorería, glosas, agenda, diagnóstico, hospitalario, farmacia, inventario, costos de nómina, presupuesto de ESE.
2. **Motor de distribución de costos** y rentabilidad por centro de costo, servicio y EPS.
3. **Auditor de configuración de SaludIPS.**
4. **Validador de precios máximos de medicamentos** (catálogo regulatorio central).
5. **Asistente IA** con cupo y cobro por excedente.
6. **Reportes en segundo plano** y alertas.

**Fuera de alcance v1:** otros sectores, conectores para ERP genéricos, escritura en los sistemas de las instituciones, app móvil nativa.

**Pilotos:** 3 instituciones: el operador farmacéutico, una IPS privada y una ESE.

## 2. Glosario mínimo
| Término | Significado |
|---|---|
| Empresa / institución | Cliente (tenant). Toda fila de negocio lleva `EmpresaId` |
| Superadmin | Administrador general de IAnalitica. Único que crea empresas |
| `Admin{NIT}` | Único administrador de cada empresa (D27) |
| Conector | Servicio de Windows dentro de SaludIPS que extrae y envía datos |
| Carga | Envío de una entidad (`etl.Carga`), procesado por lotes |
| Ventana / incremental / foto / completa | Modos de carga (contrato §8) |
| Módulo | Paquete de dashboards y funciones (lista en §5.3) |
| `[S]` | Supuesto por verificar |

## 3. Arquitectura

### 3.1 Vista general
```
[SaludIPS on‑premise] ──HTTPS (OAuth2 client credentials, lotes gzip)──► [IAnalitica.Api]  ──► SQL (stg) ──► etl.Trabajo
        ▲  catálogo regulatorio (bajada)                                                              │
        └────────────────────────────────────────────────────────────────── [IAnalitica.Worker] ◄─────┘
                                                                              │ procesa cargas, cartera, costos,
                                                                              │ auditor, validador, alertas, reportes
[Navegador] ──Identity (cookie + 2FA)──► [IAnalitica.Web (Blazor + endpoints de consulta)] ──RLS──► Azure SQL
                                                  └── Asistente IA ──► Anthropic (IChatClient)
```

### 3.2 Solución .NET 10 (`IAnalitica.sln`)
```
src/
  IAnalitica.Domain/              entidades, value objects, enums, abstracciones de módulos (sin dependencias)
  IAnalitica.Application/         casos de uso CQRS (**mediador propio**, §3.5), validaciones (FluentValidation), behaviors
                                  (autorización, módulo activo, caché, logging), interfaces de infraestructura
  IAnalitica.Infrastructure/      EF Core 10 (mapeo DB‑first, sin migraciones salvo Identity/OpenIddict),
                                  Dapper para consultas analíticas, interceptor SESSION_CONTEXT, cola SQL,
                                  Blob, Key Vault, correo, IChatClient (Anthropic), OpenTelemetry
  IAnalitica.Modulos.Financiero/  métricas, dashboards de fábrica, herramientas IA (P&G, balance, flujo, presupuesto)
  IAnalitica.Modulos.Ingreso/     ciclo del ingreso, cartera, glosas, tesorería
  IAnalitica.Modulos.Operacion/   agenda, diagnóstico, hospitalario, nómina y costos
  IAnalitica.Modulos.Farmacia/    dispensación, pendientes, inventario, validador de precios máximos
  IAnalitica.Modulos.Ese/         presupuesto público, riesgo fiscal
  IAnalitica.Modulos.Auditor/     auditor de configuración de SaludIPS (reglas)
  IAnalitica.Ingesta.Contracts/   DTO del contrato canónico — netstandard2.0 (lo usa el conector net48)
  IAnalitica.Api/                 ingesta + OpenIddict (token) + catálogo regulatorio (bajada) + prospectos
  IAnalitica.Web/                 Blazor Web App (portal) + endpoints de consulta para el portal
  IAnalitica.Worker/              BackgroundService que consume etl.Trabajo
database/
  scripts/                        DbUp: 0001… (ver §6.2)
  IAnalitica.Database.Migrator/   ejecutable DbUp (pipeline y local)
tests/
  IAnalitica.Domain.Tests/  IAnalitica.Application.Tests/  IAnalitica.Integration.Tests/ (Testcontainers SQL Server)
docs/                             esta spec y las vigentes de §16
```
**Reglas de dependencia:**
- `Domain` no depende de nada.
- `Application` depende de `Domain`.
- `Infrastructure` y los `Modulos.*` dependen de `Application` y `Domain`.
- Los hosts (`Api`, `Web`, `Worker`) componen todo.
- Ningún módulo referencia a otro: se comunican por `Application`.

### 3.3 Stack y licencias (verificar la licencia en cada versión al instalar, D32)
| Necesidad | Elección | Licencia / costo (verificado oct‑2026, fuentes secundarias) |
|---|---|---|
| CQRS | **Mediador propio** en `IAnalitica.Application` (§3.5). **Sin MediatR** (D34) | Sin licencia ni vencimiento. Mantenimiento interno |
| Validación | FluentValidation | Apache 2.0 |
| Mapeo | **Mapeo manual o Mapperly** (verificar licencia). **No AutoMapper ≥ 15** (comercial) | — |
| Acceso a datos | EF Core 10 (escritura y entidades) + **Dapper** (consultas de métricas) | MIT / Apache |
| Migraciones | **DbUp** con scripts SQL versionados | MIT |
| Autenticación de usuarios | **ASP.NET Core Identity** (cookie, 2FA TOTP) | Incluida en .NET |
| Tokens de API (client credentials) | **OpenIddict** (servidor en `IAnalitica.Api`) | Apache 2.0 |
| Excel | **ClosedXML** | MIT |
| PDF | **QuestPDF**, licencia comunitaria (gratis para ingresos < USD 1 M) | **Elegible (D33).** Configurar `QuestPDF.Settings.License = LicenseType.Community`. Revisión anual del umbral |
| UI | Blazor Web App, render **InteractiveAuto**. Componentes **MudBlazor** (MIT); gráficos **Blazor‑ApexCharts** (verificar licencia) | Sin DevExpress, Telerik ni Syncfusion (pagos) salvo aprobación |
| IA | `Microsoft.Extensions.AI` (`IChatClient`) + SDK oficial `Anthropic` | Pago por uso |
| Resiliencia HTTP | `Microsoft.Extensions.Http.Resilience` | MIT |
| Observabilidad | OpenTelemetry + distribución de Azure Monitor | Gratis; ingesta con cupo |
| API docs | OpenAPI de ASP.NET Core 10 + Scalar | MIT |
| Pruebas | xUnit, Testcontainers (SQL Server), NSubstitute, bUnit (Blazor) | Gratis (verificar licencias) |
| CI/CD | GitHub Actions | Minutos gratis limitados en repos privados: vigilar consumo |

### 3.4 Convenciones de código
- **Nombres:**
  - Comandos: `CrearEmpresaCommand` / `CrearEmpresaCommandHandler`.
  - Consultas: `ObtenerDatosWidgetQuery`.
  - Validadores: `CrearEmpresaCommandValidator`.
  - Opciones: `ConectorOptions`.
- **Resultados:** `Result<T>` propio (sin excepciones para flujos de negocio). Excepciones solo para lo excepcional.
- **Asincronía y cancelación:** asíncrono de punta a punta; `CancellationToken` en todo método público de Application e Infrastructure.
- **Inmutabilidad:** `record` para DTO y definiciones.
- **Fechas:** de negocio en **fecha local de la fuente** (`DateOnly` / `FechaKey`); marcas técnicas en **UTC** (`DateTime.UtcNow` vía `TimeProvider`).
- **Dinero:** `decimal`, nunca `double`. COP con 2 decimales en presentación.
- **Seguridad por empresa:** prohibido recibir `EmpresaId` desde el cliente. Sale de la sesión (usuario) o del token (API).
- **Logs:** `ILogger` con plantillas estructuradas. **Nunca** datos de cargas, preguntas de IA, secretos ni datos de personas (§10).

### 3.5 Mediador propio (D34)
Ubicación: `IAnalitica.Application/Mediacion/`. Sin dependencias externas. Mismo modelo mental que MediatR, para que el código sea familiar.

```csharp
namespace IAnalitica.Application.Mediacion;

public readonly record struct Unit { public static readonly Unit Value = new(); }

public interface IRequest<TResponse> { }                                  // comandos y consultas
public interface IStreamRequest<TResponse> { }                            // respuestas en streaming (chat IA por SSE)

public interface IRequestHandler<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken ct);
}

public interface IStreamRequestHandler<in TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken ct);
}

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

public interface IPipelineBehavior<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct);
}

public interface ISender
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default);
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default);
}

// Eventos internos (p. ej. CargaProcesada → encolar CALCULAR_CARTERA). Se publican en secuencia, en el mismo scope.
public interface INotification { }
public interface INotificationHandler<in TNotification> where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken ct);
}
public interface IPublisher { Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification; }
```
**Implementación (`Mediador : ISender, IPublisher`):**
1. Resuelve `IRequestHandler<TRequest, TResponse>` desde el `IServiceProvider` del scope actual. **Exactamente un** handler por request; si falta o hay más de uno, se lanza `InvalidOperationException` al arrancar (validación en el registro, no en tiempo de ejecución).
2. Envuelve el handler con los `IPipelineBehavior` registrados, **en el orden de registro**.
3. Para evitar reflexión por llamada, cachea por tipo de request un *wrapper* genérico (`RequestHandlerWrapper<TRequest, TResponse>`) en un `ConcurrentDictionary<Type, object>`. La reflexión ocurre solo la primera vez.
4. Los handlers de `IStreamRequest` no pasan por behaviors de `Task`; tienen su propio punto de logging y autorización dentro del handler.

**Registro:** `services.AddMediacion(params Assembly[] ensamblados)` busca por reflexión propia (sin librerías) las implementaciones cerradas de `IRequestHandler<,>`, `IStreamRequestHandler<,>` e `INotificationHandler<>` y las registra como `Scoped`. Los behaviors se registran explícitamente en este orden:

| # | Behavior | Responsabilidad |
|---|---|---|
| 1 | `TelemetriaBehavior` | Span de OpenTelemetry y log estructurado (nombre del request, duración, resultado). **Sin el contenido del request** |
| 2 | `AutorizacionBehavior` | Requests con `[RequierePermiso("...")]`: verifica el permiso del usuario de la sesión |
| 3 | `ModuloActivoBehavior` | Requests con `[RequiereModulo("CARTERA")]`: verifica que el módulo esté activo para la empresa |
| 4 | `ValidacionBehavior` | Ejecuta los `IValidator<TRequest>` de FluentValidation y devuelve `Result` con errores, sin excepción |
| 5 | `CacheBehavior` | Solo consultas que implementan `IConsultaCacheable` (clave + duración + versión de carga) |
| 6 | `TransaccionBehavior` | Solo comandos que implementan `IComandoTransaccional`: transacción de EF Core |

**Pruebas obligatorias** (`IAnalitica.Application.Tests/Mediacion`):
- un handler por request (falla al arrancar si falta o se duplica);
- orden de los behaviors;
- propagación de `CancellationToken`;
- excepción del handler sin envolver;
- streaming cancelable;
- notificaciones a varios handlers;
- **sin reflexión en la segunda llamada**, verificado con un contador en pruebas.

## 4. Seguridad e identidad

### 4.1 Usuarios (spec 22, D25–D27)
- Identity con usuario `int`, tablas en el esquema `seg` (`23_ddl` §1).
- Solo el superadmin crea empresas. Al crearla se genera **`Admin{NIT}`** (NIT sin DV) con **contraseña aleatoria de 14 caracteres** mostrada una vez y vencida a las 72 h. En el primer ingreso son obligatorios el cambio de contraseña y el doble factor.
- **Un solo administrador por empresa.** El `Admin{NIT}` crea usuarios de su empresa con los roles `GERENTE`, `FINANCIERO`, `CARTERA`, `TESORERO`, `FARMACIA`, `AUDITOR` y `CONSULTA`. Doble factor obligatorio para `Admin{NIT}` y superadmin; opcional para el resto (configurable por empresa).
- Política de contraseñas: mínimo 10 caracteres con mayúscula, minúscula, número y carácter especial; historial de 5; bloqueo tras 5 intentos durante 15 min; sesión inactiva de 30 min.
- **Usuario desactivado** (D35): al desactivar se cambia el `SecurityStamp`; el `SecurityStampValidator` revalida cada **1 min** y cierra la sesión. El inicio de sesión rechaza usuarios con `Activo = 0`. La BD ya no verifica `Activo` (ver §4.2).
- **Permisos** (`seg.RolPermiso`, semilla en `0130`, **[S] hasta confirmación del dueño**, D37): `dash.<modulo>` (13, uno por módulo de §5.3), `reportes.exportar`, `hallazgos.gestionar`, `ia.usar`, `admin.usuarios`, y solo superadmin `admin.empresas`, `admin.ia`, `admin.catalogos`. Matriz: SUPERADMIN todo; ADMIN_EMPRESA todos los `dash.*` + exportar, hallazgos, IA y usuarios; GERENTE todos los `dash.*` + exportar e IA; FINANCIERO financiero, ciclo del ingreso, cartera, tesorería, glosas, ESE, inventario + exportar e IA; CARTERA cartera, glosas, ciclo del ingreso + exportar; TESORERO tesorería y cartera + exportar; FARMACIA farmacia e inventario + hallazgos y exportar; AUDITOR auditor de configuración + hallazgos y exportar; CONSULTA todos los `dash.*` salvo nómina, sin exportar ni IA. `dash.nomina_costos` solo gerencia (05 §6b).
- **Primer superadmin** (D38): `IAnalitica.Database.Migrator crear-superadmin --usuario <u> --correo <c>`. Crea el usuario con contraseña aleatoria de 14 caracteres mostrada una vez, `DebeCambiarClave = 1` y doble factor obligatorio en el primer ingreso. Falla si ya existe un superadmin activo. Fija `EsSuperAdmin = 1` en el contexto de sesión solo para esa operación.
- **Kit de entrega** al crear la empresa: usuario, contraseña inicial, URL y credenciales del conector (ClientId y secreto mostrados una vez).

### 4.2 Aislamiento por empresa (D35, D36)
- **RLS** en SQL, script `database/scripts/siempre/9000_rls.sql`, que se ejecuta **en cada despliegue** (DbUp *run always*) y regenera la política `seg.PolTenant` sobre **todas** las tablas con columna `EmpresaId` de los esquemas seg, cfg, etl, stg, dim, fact, ia, rpt, reg, saludips y aud. Una tabla nueva queda cubierta sin tocar nada. `crm.*` no lleva RLS.
- **Contexto de sesión.** Un interceptor de EF Core / Dapper ejecuta en **cada** apertura de conexión `sp_set_session_context` con `@read_only = 1`:

  | Clave | Portal (usuario) | API (token) | Superadmin |
  |---|---|---|---|
  | `EmpresaId` (int) | su empresa | `empresa_id` del token | NULL |
  | `UsuarioId` (int) | su Id | NULL | su Id |
  | `EsSuperAdmin` (bit) | 0 | 0 | 1 |
  | `EsAdminPrincipal` (bit) | 1 solo `Admin{NIT}` | 0 | 0 |
  | `Autenticacion` (bit) | 1 **solo** en inicio de sesión, 2FA y cambio de clave | 1 **solo** en `POST /connect/token` | igual |

  Ninguna otra ruta fija `Autenticacion`. Sin contexto, la aplicación no ve nada.
- **Predicados:**
  - general: `rol_etl` o `EsSuperAdmin = 1` o `EmpresaId = contexto` (FILTER + BLOCK AFTER INSERT/UPDATE);
  - `ia.Tarifa`, `cfg.Dashboard`: además, lo global (`EmpresaId` NULL) es legible para todos; escribir lo global, solo superadmin;
  - `seg.Usuario`, `seg.ApiClienteEmpresa`: además, visibles y actualizables con `Autenticacion = 1` (búsqueda por nombre, intentos fallidos, bloqueo, sello);
  - `aud.*`: la auditoría de una empresa la ve solo su `Admin{NIT}`; la global, solo el superadmin; `aud.AccesoSuperadmin`, solo el superadmin. Sin BLOCK (hay eventos antes del inicio de sesión); solo inserción por permisos.
- **Principales de BD** (`0900`; los usuarios concretos los crea el ambiente, no DbUp):
  - `rol_app`: portal, API y los trabajos del worker que corren con el contexto de un usuario (reportes, IA). Sujeto a RLS.
  - `rol_etl`: worker para procesar cargas y cálculos. Sin RLS. **El worker tiene dos cadenas de conexión** (una por rol).
  - `dbo`/`sysadmin` quedan filtrados por RLS como cualquiera: las pruebas se conectan con usuarios de `rol_app`.
- **Pool de conexiones:** `sp_reset_connection` borra el contexto al devolver la conexión al pool; el interceptor lo fija de nuevo en cada apertura. **Verificar en F0 con una prueba** (dos usuarios de empresas distintas en el mismo pool).
- Doble defensa: los handlers filtran además por `EmpresaId` de la sesión. Las solicitudes de reporte de otro usuario de la misma empresa se filtran en el handler (la RLS es por empresa).
- **Pruebas obligatorias:** un usuario de la empresa A no puede leer ni escribir datos de la empresa B en ninguna tabla (lectura, UPDATE/DELETE por filtro, INSERT y cambio de `EmpresaId` bloqueados); sin contexto no se ve nada; toda tabla con `EmpresaId` está en la política (consulta a `sys.security_predicates`).

### 4.3 API de ingesta
- OpenIddict, client credentials. Cada `ClientId` se asocia a una empresa (`seg.ApiClienteEmpresa`); el `EmpresaId` sale del token.
- Rate limiting por cliente.
- HTTPS y TLS 1.2 o superior.
- El cuerpo nunca se registra en logs.

### 4.4 Secretos
- Azure Key Vault con identidad administrada. En local, User Secrets.
- Nada de secretos en `appsettings`, en la BD ni en el repositorio.
- Conector: los parámetros `.Secreto` van cifrados con DPAPI `LocalMachine` (spec 12 §5).

### 4.5 Datos personales (borradores legales en `legal/`)
- **Pacientes:** solo un hash SHA‑256 con sal calculado en el servidor de la institución.
- **Nómina:** sin nombres; solo agregados para roles de gerencia.
- **IA:** nombres de personas seudonimizados antes de enviarlos.
- Ningún dato clínico.

## 5. Módulos funcionales (resumen normativo; detalle en las specs)

### 5.1 Ingesta y conector
| Tema | Definición | Detalle |
|---|---|---|
| Contrato | Entidades, campos, modos, validaciones, errores | spec 05 (incluye §6b) |
| Extracción SaludIPS | Funciones `analitica.fn_*` en la BD de SaludIPS a partir de los SQL 06 y 11 | 06, 11 |
| Conector | `SaludIPSNet.Analitica.Conector` (net48, Worker SDK, servicio de Windows), parámetros `Analitica.*` en `SIIFNet.Parametros`, modo simulación, pantalla en SIIFNet | spec 12 |
| Entidades nuevas a añadir al contrato y al conector | `configuracion` (FOTO, auditor), `tarifas-suministros` y `catalogo-medicamentos` (farmacia), `ordenes-externas` (pendientes), `presupuesto-publico` y `contratos-publicos` (ESE) | specs 15, 16, 17 |
| Bajada | `GET /v1/regulacion/precios-maximos?desde=` → `analitica.PrecioMaximoRegulado` en SaludIPS | spec 15 §2.3 |

**Supuestos ya resueltos** (ya aplicados en 06 y 11):
- `mContratos.CodTipoContrato`: 1 Evento, 2 Capitación, 3 PGP, 4 Paquete.
- EPS ↔ tercero: `mEmpresas.CodInstitucionAdm` → `mInstituciones.CodInstitucion` → `TerceroId`.
- `EstadoEntidad`: 1 confirmado, 2 anulado.
- `DetalleCartera`: 1 CxP, 2 CxC.
- Los parámetros de SaludIPS se copian solos de una vigencia a la siguiente (D15).

### 5.2 Capa semántica y dashboards
- **Métricas** en código, por módulo (`DefinicionMetrica` / `IMetricaCalculada`, spec 14 §3).
- **Dashboards** como JSON en `cfg.Dashboard`. Los de fábrica se cargan desde el código del módulo al desplegar.
- **Editor del superadmin:** fase 2. En la fase 1, los dashboards de fábrica se versionan en código.
- **Renderizador genérico:** `DashboardHost` con un componente por `TipoWidget`. Un dashboard y sus widgets se ocultan si el módulo no está activo o si faltan datos.
- **Motor de métricas:** SQL parametrizado generado solo desde vistas `rpt.*` y columnas registradas (lista blanca). Caché por `empresa + métrica + filtros + versión de carga`.

### 5.3 Catálogo de dashboards por módulo (fase indicada)
| Módulo | Dashboards | Fase | Fuente |
|---|---|---|---|
| FINANCIERO | Gerencial; P&G con y sin distribución; balance y razones; flujo de caja (incluye 13 semanas); ejecución presupuestal gerencial | 1 | 01_v2, 10, 09 |
| CICLO_INGRESO | Dinero detenido por etapa; atendido no facturado; FE y RIPS; re‑trabajo (anulaciones) | 1 | 09, 10 |
| CARTERA | Antigüedad CxC y CxP; DSO por EPS; conciliación | 1 | 01_v2 (`usp_GenerarCorteCartera`) |
| AGENDA | Oportunidad (desde la fecha de solicitud), inasistencia y su costo, ocupación, capacidad ociosa | 1 | 10 |
| FARMACIA | Pendientes (48 h, «pendiente con stock»); cumplimiento de precios máximos | 1 (operador piloto) | 15 |
| ESE_PRESUPUESTO | Ejecución de gastos e ingresos; equilibrio; CxP presupuestales | 1 (ESE piloto) | 17 |
| AUDITOR_CONFIG | Salud de la configuración (10 reglas iniciales) | 1 | 16 |
| DIAGNOSTICO, NOMINA_COSTOS, INVENTARIO, GLOSAS, TESORERIA | Según 09 y 15 | 2 | 09, 15 |
| HOSPITALARIO | Camas, quirófano, triage | 3 | 09 §G |
| ESE avanzado | Simulador de riesgo fiscal, SIHO y CUIPO | 2 (fórmulas validadas con la ESE) | 17 |
| Transversal | Benchmarking anónimo; pronóstico y escenarios; variación precio/volumen/mezcla | 3 | 14 §2 |

### 5.4 Motor de costos
`etl.usp_CalcularBasesCosto` + `etl.usp_DistribuirCostos` (escalonado, con prueba de conservación) + vistas de rentabilidad (spec 10). Las reglas de distribución por empresa se definen en la implementación con cada piloto. Hay **bases por defecto** (personal para administración, actividades para apoyo asistencial); la institución las cambia.

### 5.5 Auditor de configuración (spec 16)
- **Fase 1, 10 reglas:** CON‑01, CON‑02, CON‑04, FE‑01, FE‑02, FE‑03, TAR‑01, TAR‑02, TAR‑06, USU‑01. Las demás van en la fase 2.
- **Estados:** `NUEVO`, `ABIERTO`, `CORREGIDO` (automático), `ACEPTADO` y `FALSO_POSITIVO`.

### 5.6 Validador de precios máximos (spec 15)
- **Fase 1:** catálogo regulatorio (`reg.*`) con la Circular 022 de 2026 desde datos.gov.co, más los factores por CUM, y las reglas R1, R2, R5 y R6.
- **Ningún hallazgo crítico** sin un factor de presentación validado.
- **Fase 2:** bajada a SaludIPS y validación en tiempo real; R3, R4, MIPRES y SISMED.

### 5.7 Asistente IA (spec 07)
- Haiku 5.5 en pruebas, Sonnet 5.5 en producción.
- Herramientas = métricas de los módulos activos.
- Cupo incluido + excedente con tope (`ia.usp_ReservarCuota` / `usp_LiquidarReserva`).
- Seudonimización de personas. Sin proveedores chinos.
- **Fase 1 = solo pruebas internas;** se abre a clientes en la fase 2.

### 5.8 Reportes en segundo plano (spec 21) y notificaciones
Todo PDF o Excel pasa por `rpt.SolicitudReporte` + `etl.Trabajo`. Aviso con `seg.Notificacion`. Archivos en Blob privado, con descarga por SAS de 15 min y caducidad de 7 días.

### 5.9 Landing (spec 13)
- Sitio estático en SmarterASP, sin precios («Cotizar»), independiente del HIS, con el caso SaludIPS.
- El formulario envía a `POST /v1/prospectos` (API) → `crm.Prospecto`, con evidencia de la autorización.

## 6. Datos

### 6.1 Fuente única del DDL
**El DDL vive solo en `database/scripts`** y está verificado contra SQL Server 2022 (rev. 1.1). Los bloques SQL de `01`, `07`, `10`, `15`, `20`, `21` y `23` quedan **reemplazados**: se conservan como registro del diseño, pero si difieren, gana `database/scripts`. Las extracciones `06` y `11` (lado SaludIPS) siguen vigentes.

Correcciones aplicadas al unificar (rev. 1.1):
- Los esquemas se crean antes que Identity (antes, 0001 fallaba porque `seg` no existía).
- `ia.Plan` → **`ia.PlanComercial`** (`PLAN` es palabra reservada de T‑SQL; el script no compilaba).
- `etl.Carga` alineada con el contrato 05: modos `COMPLETA`, `VENTANA`, `INCREMENTAL`, `FOTO`, `INICIAL` (+ `REPROCESO` de plataforma); estados `ABIERTA`, `CERRADA`, `PROCESANDO`, `OK`, `ERROR`, `RECHAZADA` (atómica, sin `PARCIAL`); columnas `Corte`, `VersionFuente`, `TotalLotes`, `TotalFilas`, `SumaControl`, `CerradaEn`, `TraceParent`.
- `stg.Payload`: `NumeroLote` y `UNIQUE (CargaId, NumeroLote)` para el reintento idempotente.
- `etl.Trabajo`: `EmpresaId` admite NULL (trabajos globales) y se agrega `UsuarioId` (trabajos con el contexto del solicitante).
- Columnas del contrato que faltaban: `dim.Sede.NombreCorto`; `dim.CentroCosto.TipoOrigen`, `UnidadNegocio`; `dim.Tercero.DigitoVerificacion`, `ClaseOrigen`; `dim.Pagador.CodigoAdministradora`, `EsParticular`, `Activo`; `dim.Servicio.CodigoEstandar`; `dim.Articulo.CodigoCum`; `fact.TesoreriaMovimiento.ConceptoCodigo`.
- `fact.HallazgoTarifario.Estado` con CHECK y el mismo ciclo del auditor (`NUEVO`…`FALSO_POSITIVO`).
- RLS rediseñada (D35, D36) y semillas nuevas: roles, matriz de permisos (D37) y plan `PILOTO`.

### 6.2 Scripts DbUp (`database/scripts`)
| Script | Contenido |
|---|---|
| 0001 | Esquemas: seg, cfg, etl, stg, dim, fact, rpt, aud, ia, reg, saludips, crm (idempotente) |
| 0005 | Identity + OpenIddict: **se genera en F0** con `dotnet ef migrations script` desde el `DbContext` de identidad (nombres de tablas en `seg`, §4.1 y 23 §1) |
| 0020 | `seg.Empresa`, `EmpresaContacto`, `EmpresaModulo`, `RolPermiso`, `UsuarioAlcance`, `HistorialClave`, `ApiClienteEmpresa`, `Notificacion`; `cfg.EmpresaParametro`, `KpiMeta`, `Dashboard`, `ReglaConfiguracion`, `ReglaConfiguracionEmpresa` |
| 0021 | Restricciones sobre `seg.Usuario` (empresa/superadmin, FK, un admin principal por empresa) |
| 0030 | `etl.Carga`, `Rechazo`, `PeriodoCierre`, `Trabajo`; `stg.Payload` |
| 0040 | Dimensiones (incluye `Especialidad`, `Profesional`, `FuenteFinanciacion`, `RubroPresupuestal`) |
| 0050 | Hechos financieros |
| 0060 | Hechos operativos |
| 0070 | Hechos ESE |
| 0080 | `reg.*` + `fact.HallazgoTarifario` |
| 0090 | `fact.HallazgoConfiguracion`. Las tablas `saludips.*` van en F4 (script 1000+) |
| 0100 | `ia.*` (tablas y semilla de proveedores y modelos) |
| 0110 | `rpt.SolicitudReporte`, `aud.*`, `crm.Prospecto` |
| 0120 | Costeo: `cfg.CostoBase` (con semilla), `CostoRegla`, `fact.CostoBaseValor`, `CostoDistribuido` |
| 0130 | `dim.Fecha` 2015–2035, roles, matriz de permisos [S], plan `PILOTO` [S] (idempotente) |
| 0140 | Vistas `rpt.*` e `ia.vw_ConsumoMes` |
| 0150 | Funciones y procedimientos (cartera, costos, cuota y cierre de IA) |
| 0900 | Roles de BD `rol_app` y `rol_etl` y permisos por esquema (DENY UPDATE/DELETE sobre `aud`) |
| `siempre/9000` | **RLS**, en cada despliegue (§4.2) |

- Los scripts numerados se ejecutan **una sola vez** (registro de DbUp). **Nunca se edita un script ya aplicado:** se agrega uno nuevo (1000 en adelante). Hasta el primer despliegue de F0, todavía se pueden corregir.
- Los semillas de las 10 reglas del auditor (`cfg.ReglaConfiguracion`) van en F4, con los textos de corrección y rutas validados.

### 6.3 Compatibilidad con la base gratuita de Azure SQL
General Purpose serverless admite RLS y columnstore. Límite de **32 GB** y cupo de vCore‑segundos: medir el consumo en los pilotos (§11) y **configurar la pausa al agotar el cupo**.

## 7. API

### 7.1 `IAnalitica.Api` (versionada en `/v1`)
| Grupo | Endpoints |
|---|---|
| Token | `POST /connect/token` (client credentials) |
| Ingesta | `POST /v1/cargas`, `POST /v1/cargas/{id}/lotes/{n}`, `POST /v1/cargas/{id}/cerrar`, `GET /v1/cargas/{id}`, `GET /v1/cargas/{id}/rechazos`, `GET /v1/estado` |
| Bajada | `GET /v1/regulacion/precios-maximos?desde=` |
| Prospectos | `POST /v1/prospectos` (anónimo, con captcha o *honeypot* y rate limit estricto) |

Todas las entidades usan **un solo flujo de cargas**, con el nombre de la entidad en `POST /v1/cargas`. No hay una URL por entidad; el conector solo configura `Analitica.UrlBase`.

### 7.2 Endpoints de consulta del portal (`IAnalitica.Web`, autenticados por cookie)
`/q/dashboards/{codigo}`, `/q/widgets/{codigo}/datos`, `/q/reportes` (crear, listar, descargar), `/q/hallazgos`, `/q/asistente/conversaciones/{id}/mensajes` (SSE), `/q/admin/*` (superadmin y `Admin{NIT}`).

### 7.3 Errores
Problem Details (RFC 9457) con los códigos de la spec 05 §7.

## 8. Procesamiento y concurrencia (spec 20)
- Toda tarea pesada pasa por `IColaTrabajos`. En los pilotos la cola es SQL (`etl.Trabajo`). La cola de Storage se activa solo con aprobación.
- Cadena nocturna por empresa: `PROCESAR_CARGA` (por entidad) → `CALCULAR_CARTERA` → `AUDITAR_CONFIG` → `VALIDAR_PRECIOS` → `DISTRIBUIR_COSTOS` (si el periodo cerró) → `ALERTAS`.
- **Worker:** máximo **2** trabajos simultáneos en pruebas, configurable. Procesa con `rol_etl`; reportes e IA, con `rol_app` y el contexto (`EmpresaId`, `UsuarioId`) del solicitante guardado en `etl.Trabajo`. Exclusión por `ClaveExclusion`; reintentos 1/2/4/8/16 min; estado `VENENO` con alerta.

## 9. Interfaz (portal)
- Blazor Web App con render **InteractiveAuto**. Tema MudBlazor con los **colores y el logo de la empresa** (`seg.Empresa`).
- **Navegación:** selector de empresa (solo superadmin, con registro en `aud.AccesoSuperadmin` y motivo obligatorio), menú por módulos activos, campana de notificaciones, «Mis reportes», panel lateral del asistente (fase 2 para clientes).
- **Accesibilidad:** WCAG 2.2 AA en contraste y teclado. Español (Colombia). Formato numérico `es-CO`.

## 10. Observabilidad y auditoría (spec 19)
- OpenTelemetry (trazas, métricas, logs) → Azure Monitor, con muestreo de 100 % de errores y 10 % del resto en producción, y límite diario de ingesta.
- `traceparent` desde el conector.
- Procesador de limpieza obligatorio, con pruebas.
- Auditoría de negocio en `aud.*`: solo inserción.

## 11. Infraestructura (D19, D32; spec 18)
- **Azure. Región pendiente** (concepto del abogado). El desarrollo puede usar cualquier región.
- **Pruebas:**
  - Azure SQL Database con oferta gratuita, pausa al agotar el cupo;
  - Azure Container Apps por consumo para Api, Web y Worker (tres apps, escala a cero);
  - Blob LRS;
  - Key Vault;
  - Log Analytics dentro del cupo gratuito;
  - presupuesto de **USD 20** con alertas al 50, 80 y 100 %.
- **Producción:** misma plataforma (Container Apps) con réplicas mínimas y base de datos de pago. **Solo con cotización aprobada.**
- **Despliegue:**
  - GitHub Actions: compilar → pruebas → migrador DbUp → desplegar.
  - Ambientes `dev` y `prod`, con aprobación manual para `prod`.

## 12. Calidad: definición de terminado (cada historia o PR)
1. Compila sin advertencias nuevas. Pruebas unitarias del caso de uso y **prueba de aislamiento por empresa** si toca datos.
2. Pruebas de integración con Testcontainers para SQL nuevo (procedimientos, RLS, vistas).
3. **Cuadre:** toda métrica financiera nueva tiene una prueba contra un caso conocido y, en el piloto, contra el reporte de SaludIPS indicado en 03 §5 (diferencia ≤ 0,5 % explicada).
4. Sin secretos ni datos personales en logs (prueba del procesador de limpieza).
5. Los `[S]` usados quedan listados en el PR.
6. Documentación actualizada en `docs/` si cambia el comportamiento.
7. **Costo:** si el PR crea o cambia un recurso de Azure, lleva la estimación y la aprobación (D32).

## 13. Plan de implementación (fases y criterios de aceptación)
| Fase | Entregables | Criterio de aceptación |
|---|---|---|
| **F0 · Base** | Solución, CI, migrador DbUp (scripts de §6.2 + generar 0005 + `crear-superadmin`), Identity + 2FA, alta de empresa con kit de entrega, RLS con pruebas, OpenTelemetry local | Crear 2 empresas; el `Admin{NIT}` de A no ve nada de B (prueba automatizada) |
| **F1 · Conector en simulación** | Proyecto en SaludIPS, parámetros `Analitica.*`, funciones `analitica.fn_*` para catálogos, asientos, facturas y configuración; modo simulación (JSON a disco) | Los JSON de un piloto real cuadran contra `SIPS_5` y `ESTADORESULTADOSCENTROCOSTOHORIZONTAL` del mismo periodo |
| **F2 · Ingesta** | API de cargas (token, lotes, cerrar, estado), cola SQL, worker `PROCESAR_CARGA`, cartera | Carga de 13 meses del piloto; la cartera cuadra con `CARTERAESTADOCUENTAEDADES` (≤ 0,5 %) |
| **F3 · Dashboards fase 1** | Capa semántica, renderizador, dashboards de §5.3 fase 1, reportes en segundo plano | Los 3 pilotos usan los dashboards; criterios del acuerdo de piloto |
| **F4 · Auditor y validador** | 10 reglas del auditor; catálogo regulatorio + R1, R2, R5, R6 | ≥ 80 % de los hallazgos revisados confirmados como reales |
| **F5 · Costos e IA interna** | Motor de distribución con los pilotos; asistente IA en pruebas internas | Conservación del costo; consumo de IA dentro del tope |
| **F6 · Despliegue piloto** | Azure de pruebas (gratis), conector instalado en las 3 instituciones | 95 % de noches con carga correcta durante 30 días |

## 14. Pendientes que no bloquean el inicio
| # | Pendiente | Responsable | Bloquea |
|---|---|---|---|
| P1 | Región de Azure (concepto legal) | Dueño + abogado | F6 |
| P2 | Validar los `[S]` de 06, 11 y 15 (fechas de atención, `VrCosto`, `DiaHorario`, `TipoPersona`, `ClaseTercero`, `TipoCentroCosto`, `EstadoEntidad = 0`, factores por CUM) | Equipo funcional | Las métricas que los usan |
| P3 | Cobertura de `dFacturasServicios` (2 consultas en 02) | Equipo técnico | Método de enlace de facturas (ya hay respaldo) |
| P4 | **Resuelto (D33, D34).** QuestPDF comunitaria; MediatR descartado (mediador propio) | — | — |
| P5 | Revisión y firma de los documentos legales | Abogado | Contratos y pilotos |
| P6 | Fórmulas de categorización del riesgo de la ESE | ESE piloto | Simulador (fase 2) |
| P7 | Reglas de distribución de costos por piloto | Cada piloto | F5 |
| P8 | Dominio y marca de IAnalitica | Dueño | Landing |
| P9 | (F2: `GET /v1/estado` y sus códigos de error ya están en el contrato 05 rev. 1.1.) Agregar al contrato 05 las entidades `configuracion` (FOTO), `tarifas-suministros`, `catalogo-medicamentos`, `ordenes-externas`, `presupuesto-publico` y `contratos-publicos`, con sus tablas destino (farmacia no tiene hechos definidos) | Diseño (Opus) | F1 (`configuracion`), F4 y ESE |
| P10 | Confirmar la matriz rol × permiso (§4.1, D37) | Dueño | Nada (se corrige con un script nuevo) |
| P11 | Planes comerciales de IA y su cupo (hoy solo `PILOTO` con cupo 0) | Dueño | Abrir la IA a clientes (fase 2) |

## 15. Riesgos principales (y su control)
| Riesgo | Control |
|---|---|
| Cifras que no cuadran → pérdida de confianza | Cuadre obligatorio contra los reportes de SaludIPS antes de mostrar un dashboard a un cliente |
| Falsos positivos en el auditor o el validador | Arranque con reglas de alta certeza, estado `FALSO_POSITIVO` y factores validados |
| Fuga de datos entre empresas | RLS + filtro en los handlers + pruebas automatizadas en cada PR |
| Costo de nube o IA fuera de control | D32, presupuesto con alertas, topes de IA y cupo gratuito con pausa |
| Licencias comerciales sin notarlo | Tabla §3.3 y verificación en cada versión |
| Alcance creciente | Las fases de §13; lo nuevo se agrega como fase posterior |

## 16. Estado de los documentos
| Documento | Estado |
|---|---|
| `database/scripts/*` | **Vigente: fuente única del DDL** (§6.1) |
| `01_esquema_analitico_salud_v2.sql` | **Reemplazado** por `database/scripts` (DDL); se conserva como registro |
| `02_mapeo_SaludIPS.md`, `03_analisis_reportes_SaludIPS.md` | **Vigentes como referencia de reglas.** Su afirmación sobre el presupuesto público queda reemplazada por la 17 |
| `05_contrato_api_ingesta.md` | **Vigente** |
| `06_`, `11_extraccion` | **Vigentes** con las correcciones de §5.1 |
| `07_spec_asistente_ia.md` (rev. 2) | **Vigente** (`ia.Plan` se llama `ia.PlanComercial`) |
| `07_asistente_ia.sql` | **Reemplazado** por `database/scripts` |
| `09_catalogo_kpi_gerencial.md` | **Vigente** (catálogo de KPI) |
| `10_hechos_operativos_y_costeo.sql` | **Reemplazado** por `database/scripts` |
| `12_spec_conector_saludips.md` | **Vigente** |
| `13_spec_landing_page.md` | **Vigente** |
| `14_spec_capa_semantica_dashboards.md` | **Vigente** |
| `15_` (farmacia), `16_` (auditor), `17_` (ESE), `18_` (infra), `19_` (observabilidad), `20_` (colas), `21_` (reportes), `22_` (usuarios) | **Vigentes**, salvo sus bloques SQL (reemplazados por `database/scripts`) y lo que §4.2 cambia de la RLS |
| `23_ddl_complementario.sql` | **Reemplazado** por `database/scripts` (la RLS, por `siempre/9000` y §4.2) |
| `08_consentimiento_proveedor_ia.md` y `legal/` | Borradores para el abogado |

## 17. Registro de decisiones
### Producto y negocio
| # | Decisión |
|---|---|
| D01 | Producto: **IAnalitica**. Enfoque **solo sector salud** (IPS privadas, ESE públicas y operadores/dispensadores farmacéuticos). |
| D02 | **Paga la institución** (IPS, ESE u operador), cada una por su cuenta. |
| D03 | **La implementación incluye** configuración (mapeo, cuadre, reglas) y capacitación. |
| D04 | **Pilotos:** 3 instituciones. (1) operador y dispensador farmacéutico; (2) IPS privada con años de relación; (3) **IPS pública (ESE)** con años de relación. |
| D05 | Landing: **sin precios**, solo «Cotizar». Se presenta **independiente del HIS**. **Caso de éxito: SaludIPS**, describiendo la integración; sin cifras de mejora hasta que se midan y se autoricen. |
| D06 | Landing informativa en el hosting de **SmarterASP** (dominio propio). La plataforma IAnalitica se aloja aparte. |

### Arquitectura
| # | Decisión |
|---|---|
| D10 | .NET 10, Clean Architecture, CQRS con mediador propio (D34), EF Core, SQL Server, Blazor. Repo: `Juanprojas1982/IAnalitica`. |
| D11 | Una BD central multiempresa con `EmpresaId` y Row‑Level Security. Usuario de **una sola empresa**; superadmin ve todas. |
| D12 | Usuarios con **ASP.NET Core Identity** (no Entra). Doble factor obligatorio para administradores y superadmin. |
| D13 | Ingesta por API (OAuth2 client credentials). En el conector, «usuario y contraseña» = ClientId y ClientSecret. |
| D14 | **Conector dentro de SaludIPS** (`SaludIPSNet.Analitica.Conector`, net48, servicio de Windows), configurado en los parámetros de SaludIPS. SaludIPS está **on‑premise en cada institución**; los servidores **tienen salida a internet**. |
| D15 | Los parámetros de SaludIPS **se copian solos a la nueva vigencia** antes de fin de año. Riesgo cerrado; la regla PAR‑01 del auditor queda como control. |
| D16 | Dashboards declarativos sobre una capa semántica de métricas, con renderizador genérico. Perfiles de institución (ambulatoria, diagnóstica, hospitalaria, farmacéutica, **ESE pública**) por módulos activos. |
| D17 | **Auditor de configuración de SaludIPS** (solo lectura, sugiere y hace seguimiento; la institución corrige). |
| D18 | **Validador de precios máximos de medicamentos:** catálogo regulatorio central en IAnalitica, bajada a SaludIPS y hallazgos. |
| D19 | **Hosting de IAnalitica: Azure (confirmado).** Arquitectura inicial en `18_infraestructura.md`. **Región pendiente:** se decide con el concepto del abogado sobre la transferencia internacional (candidatas: Brazil South, Chile Central, Mexico Central, East US 2) y verificando la disponibilidad de Azure SQL Database. |

### Asistente IA
| # | Decisión |
|---|---|
| D20 | Proveedor Anthropic: **Haiku 5.5 en pruebas, Sonnet 5.5 en producción**. **Sin proveedores chinos**; el consentimiento queda listo por si cambia. |
| D21 | Cobro en dos niveles: **cupo incluido en el canon mensual** + **consumo adicional** a una tarifa por token configurable en parámetros, con tope autorizado. |

### Rendimiento
| # | Decisión |
|---|---|
| D23 | **Cola de trabajos** detrás de `IColaTrabajos`. Pilotos: cola en SQL (`etl.Trabajo`, $0 adicional). Crecimiento: Azure Storage Queues (previa cotización). Exclusión de un trabajo activo por empresa y entidad, reintentos con espera exponencial, cola de mensajes «venenosos», cálculos pesados en el worker y caché por versión de carga. Ver `20_spec_colas_concurrencia_rendimiento.md`. |

| D24 | **Reportes para imprimir o descargar siempre en segundo plano:** cola, estados (pendiente, en proceso, listo, error, expirado), descarga posterior desde «Mis reportes», límites por usuario y reutilización de resultados idénticos. Ver `21_spec_reportes_en_segundo_plano.md`. |

### Empresas y usuarios
| # | Decisión |
|---|---|
| D25 | Solo el superadmin crea empresas. Al crearla se genera su administrador principal **`Admin{NIT}`**, que cambia su contraseña y crea los usuarios de su empresa. Ver `22_spec_alta_empresa_y_usuarios.md`. |
| D26 | **Contraseña inicial aleatoria por empresa** (14 caracteres), mostrada una sola vez al superadmin, que vence en 72 h, con **cambio y doble factor obligatorios** en el primer ingreso. Se aplica igual a todo usuario nuevo o restablecido. |
| D27 | **Un solo administrador por empresa: `Admin{NIT}`.** El rol `ADMIN_EMPRESA` no se puede asignar a otros usuarios. Solo el superadmin puede reemplazar el `Admin{NIT}` (por ejemplo, si la persona sale de la empresa). |

### Observabilidad
| # | Decisión |
|---|---|
| D22 | **Logs, trazas y métricas con OpenTelemetry**, exportados a Azure Monitor (Application Insights / Log Analytics) con muestreo. La auditoría de negocio (accesos, cambios administrativos) sigue en tablas SQL `aud.*`. Ver `19_spec_observabilidad.md`. |

### Proceso
| # | Decisión |
|---|---|
| D30 | Análisis y specs con **Opus**; implementación con **Sonnet**. |
| D33 | **Licencias comunitarias:** la empresa factura **menos de USD 1 M al año** (declarado por el dueño, 2026‑10‑26). Se usa **QuestPDF** (comunitaria, umbral USD 1 M). **Revisión anual:** si los ingresos se acercan a USD 1 M, se evalúa la licencia pagada o la alternativa HTML → PDF antes de superar el umbral (QuestPDF da 90 días de transición). |
| D34 | **Mediador propio, sin MediatR**, para no depender de licencias ni renovaciones de terceros. Diseño en §3.5. La clave comunitaria de MediatR obtenida no se usa. **Criterio general:** preferir componentes de .NET o con licencia permisiva (MIT/Apache) sin vencimientos. |
| D35 | **RLS por contexto de sesión** (rev. 1.1). El predicado lee `EmpresaId`, `EsSuperAdmin`, `EsAdminPrincipal` y `Autenticacion` desde `SESSION_CONTEXT` en vez de consultar `seg.Usuario`. Motivos: `seg.Usuario` quedaba fuera de la RLS (fuga de la lista de usuarios), la API no tiene `UsuarioId` (sus inserciones quedaban bloqueadas) y el predicado consultaba una tabla por fila. Consecuencia aceptada: la desactivación la controla la aplicación (§4.1). Dos roles de BD; el worker usa ambos (§4.2). |
| D36 | **RLS en un script que corre en cada despliegue** (`siempre/9000`) para que las tablas agregadas en scripts posteriores queden cubiertas sin editar scripts aplicados. |
| D37 | **Matriz rol × permiso** inicial (§4.1). **[S] hasta confirmación del dueño.** |
| D38 | **Primer superadmin** por comando del migrador (§4.1). Plan de IA por defecto `PILOTO` con cupo 0 **[S]**. |
| D32 | **Costo primero.** Antes de crear cualquier recurso pago (nube, licencia, servicio, API) se hace **doble revisión de precio**: (1) precio en la página oficial o la calculadora del proveedor, con fecha; (2) estimado mensual para el escenario de uso real. Se presenta y se espera **tu aprobación explícita**. En la etapa de pruebas, se usan solo niveles gratuitos o mínimos. |
| D31 | Documentos legales: los entrega IAnalitica (Claude) como **borradores**; los revisa y firma un abogado. |
