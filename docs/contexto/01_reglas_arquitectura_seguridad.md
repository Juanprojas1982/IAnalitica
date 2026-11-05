# IAnalitica — Reglas de uso, producto, arquitectura, seguridad y RLS

Parte de `docs/CONTEXTO.md` (índice). La numeración §N es global y se conserva entre archivos. Precedencia y reglas de uso: `01_reglas_arquitectura_seguridad.md` §0.

## 0. Reglas de uso (obligatorias)
1. **Precedencia:** (1) este documento → (2) specs de detalle fusionadas en §15–§28 → (3) código y `database/scripts`. Si chocan, gana el de mayor precedencia, **salvo el DDL: manda `database/scripts`**.
2. **No inventar.** Lo que no esté en `docs/contexto/` se pregunta. No asumir tablas, reglas de negocio ni paquetes NuGet.
3. **`[S]` = supuesto no confirmado.** Se implementa tras bandera o con estado «por verificar», nunca como dato confirmado; se lista en el PR.
4. **Costo (D32).** Antes de crear un recurso pago (Azure, licencia, API): precio oficial con fecha + estimado mensual + **aprobación explícita del dueño**. En pruebas, solo niveles gratuitos.
5. **Modelos (D30).** Diseño con Opus, código con Sonnet. Un cambio de diseño durante la implementación vuelve a diseño.
6. **Idioma:** dominio, BD, mensajes y docs en español; sufijos técnicos .NET en inglés (`Handler`, `Options`).
7. **Definición de terminado:** compila sin advertencias (`TreatWarningsAsErrors`); prueba unitaria del caso de uso; **prueba de aislamiento por empresa** si toca datos; integración con Testcontainers para SQL nuevo; métrica financiera nueva cuadrada contra un caso conocido (≤ 0,5 % explicado); sin secretos ni datos personales en logs; `[S]` listados; docs actualizadas; estimación y aprobación si cambia un recurso de Azure.
8. **Nunca editar un script DbUp ya aplicado:** se agrega uno nuevo (1000+).

## 1. Producto
SaaS de analítica gerencial **solo sector salud** (IPS privadas, ESE públicas, operadores/dispensadores farmacéuticos). Recibe datos del HIS (SaludIPS primero; contrato canónico para otros después).
Entrega: dashboards por módulo · motor de distribución de costos y rentabilidad · auditor de configuración de SaludIPS · validador de precios máximos de medicamentos · asistente IA con cupo y cobro por excedente · reportes en segundo plano.
**Fuera de alcance v1:** otros sectores, ERP genéricos, escritura en sistemas de las instituciones, app móvil nativa.
**Pilotos (3):** operador farmacéutico, IPS privada, ESE. Paga cada institución por su cuenta (D02); la implementación incluye configuración y capacitación (D03).

Glosario: *Empresa* = tenant (toda fila de negocio lleva `EmpresaId`) · *Superadmin* = único que crea empresas · *`Admin{NIT}`* = único administrador de cada empresa · *Conector* = servicio Windows dentro de SaludIPS · *Carga* = envío de una entidad (`etl.Carga`) por lotes · modos de carga: `COMPLETA`, `VENTANA`, `INCREMENTAL`, `FOTO`, `INICIAL` (+ `REPROCESO` interno).

## 2. Arquitectura
```
[SaludIPS on-premise] --HTTPS OAuth2 client credentials, lotes gzip--> [Api] -> stg -> etl.Trabajo -> [Worker]
[Navegador] --Identity cookie + 2FA--> [Web Blazor + /q endpoints] --RLS--> SQL Server
Web --> Asistente IA --> Anthropic (IChatClient)
```
Solución .NET 10 (`IAnalitica.sln`): `Domain` (sin dependencias) · `Application` (CQRS con mediador propio, FluentValidation, behaviors) · `Infrastructure` (EF Core 10 DB-first + Dapper para métricas, cola SQL, IChatClient) · `Modulos.{Financiero,Ingreso,Operacion,Farmacia,Ese,Auditor}` · `Ingesta.Contracts` (netstandard2.0, lo usa el conector net48) · hosts `Api`, `Web`, `Worker` · `database/scripts` + `database/IAnalitica.Database.Migrator` (DbUp) · `tests/` (Domain, Application, Integration con Testcontainers SQL Server 2022).
**Dependencias:** `Domain` ← `Application` ← `Infrastructure`/`Modulos.*` ← hosts. Ningún módulo referencia a otro; se comunican vía `Application`.

### Stack (licencias verificar al instalar, D32)
| Necesidad | Elección | Nota |
|---|---|---|
| CQRS | **Mediador propio** `Application/Mediacion` | **Sin MediatR** (D34) |
| Validación | FluentValidation | Apache 2.0 |
| Mapeo | Manual o Mapperly (licencia por verificar) | **No AutoMapper ≥ 15** (comercial) |
| Datos | EF Core 10 (escritura) + Dapper (métricas) | |
| Migraciones | DbUp | |
| Usuarios | ASP.NET Core Identity (cookie, 2FA TOTP) | no Entra (D12) |
| Tokens API | OpenIddict (client credentials) | |
| Excel / PDF | Excel escrito sobre OpenXML a mano (F3); PDF **QuestPDF 2026.8.0 comunitaria** | Licencia: ingresos < USD 1 M (D33); sector público y empresas en bolsa no califican; revisión anual |
| UI | Blazor SSR + estado en URL + SVG (F3) | MudBlazor, ApexCharts y `InteractiveAuto` **no adoptados** (ver §10) |
| IA | `Microsoft.Extensions.AI.Abstractions` 10.11.0 (`IChatClient`) | SDK `Anthropic` 13.3.0 existe en NuGet; **no instalado ni probado** |
| Observabilidad | OpenTelemetry (exportador consola/OTLP) | Azure Monitor no agregado |
| Pruebas | xUnit, Testcontainers, NSubstitute, bUnit | |
| CI | GitHub Actions | |

### Mediador
`IRequest<T>`, `IRequestHandler<,>`, `IStreamRequest<T>`, `IPipelineBehavior<,>`, `ISender`, `INotification`, `IPublisher`. Exactamente un handler por request (falla al arrancar si falta o se duplica). Registro: `AddMediacion(ensamblados)`; handlers `Scoped`. Behaviors en este orden: Telemetría → Autorización (`[RequierePermiso]`) → ModuloActivo (`[RequiereModulo]`) → Validación → Caché (`IConsultaCacheable`) → Transacción (`IComandoTransaccional`).

### Convenciones de código
Comandos `XxxCommand`/`XxxCommandHandler`, consultas `XxxQuery`, validadores `XxxCommandValidator`, opciones `XxxOptions` · `Result<T>` propio para flujos de negocio · `async` + `CancellationToken` en todo método público de Application/Infrastructure · `record` para DTO · fechas de negocio `DateOnly`/`FechaKey` (fecha local de la fuente); marcas técnicas UTC vía `TimeProvider`; «hoy» de negocio = hora de Colombia (UTC−5 fijo) · dinero `decimal` (nunca `double`) · **prohibido recibir `EmpresaId` del cliente** (sale de sesión o token) · logs estructurados **sin** datos de cargas, preguntas de IA, secretos ni datos de personas.

## 3. Seguridad e identidad
- Identity con usuario `int`, tablas en esquema `seg`. Solo el superadmin crea empresas; se genera `Admin{NIT}` (NIT sin DV), contraseña aleatoria de 14 caracteres mostrada una vez, vence en 72 h, cambio de clave y 2FA obligatorios en el primer ingreso. **Un solo administrador por empresa** (`ADMIN_EMPRESA` no asignable a otros; solo el superadmin lo reemplaza).
- `Admin{NIT}` crea usuarios con roles `GERENTE`, `FINANCIERO`, `CARTERA`, `TESORERO`, `FARMACIA`, `AUDITOR`, `CONSULTA`. 2FA obligatorio para `Admin{NIT}` y superadmin; para el resto hoy **no hay parámetro por empresa** (`[S]`).
- Contraseñas: ≥ 10 caracteres con mayúscula, minúscula, número y especial; historial 5; bloqueo 15 min tras 5 intentos; sesión inactiva 30 min; 20 solicitudes/min por IP en ingreso.
- Usuario desactivado: cambia `SecurityStamp`, revalidación cada 1 min, login rechaza `Activo = 0`. La BD no verifica `Activo`.
- Nombres de usuario `[S]`: 4–60 caracteres `[A-Za-z0-9._-]`, **únicos globalmente**, sin prefijo `Admin`. `seg.Empresa.Codigo` = NIT. ClientId del conector = `conector-{NIT}`.
- **Permisos `[S]` (D37, pendiente P10):** `dash.<módulo>` (13), `reportes.exportar`, `hallazgos.gestionar`, `ia.usar`, `admin.usuarios`; solo superadmin: `admin.empresas`, `admin.ia`, `admin.catalogos`. Matriz: SUPERADMIN todo · ADMIN_EMPRESA todos los `dash.*` + exportar, hallazgos, IA, usuarios · GERENTE todos los `dash.*` + exportar, IA · FINANCIERO financiero, ciclo del ingreso, cartera, tesorería, glosas, ESE, inventario + exportar, IA · CARTERA cartera, glosas, ciclo del ingreso + exportar · TESORERO tesorería, cartera + exportar · FARMACIA farmacia, inventario + hallazgos, exportar · AUDITOR auditor de configuración + hallazgos, exportar · CONSULTA todos los `dash.*` salvo nómina, sin exportar ni IA. `dash.nomina_costos` solo gerencia. La gestión de costos usa `admin.usuarios` (`[S]`, sin permiso propio).
- **Primer superadmin:** `IAnalitica.Database.Migrator crear-superadmin --usuario <u> --correo <c>` (falla si ya existe uno activo).

### Aislamiento por empresa (RLS)
- Script `database/scripts/siempre/9000_rls.sql` corre **en cada despliegue** y regenera la política `seg.PolTenant` sobre todas las tablas con `EmpresaId` de los esquemas seg, cfg, etl, stg, dim, fact, ia, rpt, reg, saludips, aud. `crm.*` sin RLS.
- Un interceptor ejecuta `sp_set_session_context` (`@read_only = 1`) en **cada** apertura de conexión: `EmpresaId`, `UsuarioId`, `EsSuperAdmin`, `EsAdminPrincipal`, `Autenticacion` (1 solo en login/2FA/cambio de clave y en `POST /connect/token`). Sin contexto no se ve nada.
- Predicado general: `rol_etl` o `EsSuperAdmin = 1` o `EmpresaId = contexto` (FILTER + BLOCK AFTER INSERT/UPDATE). `ia.Tarifa` y `cfg.Dashboard`: lo global (`EmpresaId` NULL) legible por todos, escribible solo por superadmin. `seg.Usuario` y `seg.ApiClienteEmpresa`: visibles con `Autenticacion = 1`. `aud.*`: la de empresa la ve su `Admin{NIT}`; la global y `aud.AccesoSuperadmin`, solo superadmin; sin BLOCK; solo inserción (DENY UPDATE/DELETE).
- Roles de BD: `rol_app` (portal, API, reportes e IA; sujeto a RLS) y `rol_etl` (worker para cargas y cálculos; sin RLS). **El worker tiene dos cadenas de conexión.** `dbo`/`sysadmin` también quedan filtrados; las pruebas usan `rol_app`.
- Defensa doble: los handlers filtran además por `EmpresaId` de sesión. Las pruebas de aislamiento (lectura, UPDATE/DELETE, INSERT, cambio de `EmpresaId`, sin contexto, cobertura en `sys.security_predicates`) son obligatorias en cada PR que toque datos.
- Datos personales: pacientes solo con hash SHA-256 con sal calculado en la institución; **nómina sin nombres** (solo agregados para gerencia); IA con nombres seudonimizados; ningún dato clínico. Procesador de limpieza de logs enmascara números de ≥ 6 dígitos.
- Secretos: Key Vault con identidad administrada (Azure) / User Secrets (local); nunca en `appsettings`, BD ni repo. Conector: parámetros `.Secreto` cifrados con DPAPI `LocalMachine`.

