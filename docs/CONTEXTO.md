# IAnalitica — CONTEXTO VIGENTE

Fuente única de contexto para humanos y agentes de IA. Consolida la spec maestra v1.1, las notas F0–F5 y el registro de decisiones.
Estado: rama `feature/f5-costos-ia` (PR #7). Fases F0–F5 implementadas; F6 sin iniciar. Actualizado: 2026-11-05.
Histórico (no usar como fuente): `docs/archivo/`.

## 0. Reglas de uso (obligatorias)
1. **Precedencia:** (1) este documento → (2) specs de detalle de §14 → (3) código y `database/scripts`. Si chocan, gana el de mayor precedencia, **salvo el DDL: manda `database/scripts`**.
2. **No inventar.** Lo que no esté aquí ni en una spec de §14 se pregunta. No asumir tablas, reglas de negocio ni paquetes NuGet.
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

## 4. Datos
- **DDL único = `database/scripts`** (verificado SQL Server 2022). Los `.sql` de `docs/` y de `archivo/` son registro histórico.
- Scripts: 0001 esquemas · 0005 Identity+OpenIddict (generado con EF; CI verifica que coincida) · 0020 seg/cfg · 0021 restricciones `seg.Usuario` · 0030 etl/stg · 0040 dimensiones · 0050 hechos financieros · 0060 operativos · 0070 ESE · 0080 `reg.*` + `fact.HallazgoTarifario` · 0090 `fact.HallazgoConfiguracion` · 0100 `ia.*` · 0110 `rpt.SolicitudReporte`, `aud.*`, `crm.Prospecto` · 0120 costeo · 0130 `dim.Fecha` 2015–2035, roles, permisos `[S]`, plan `PILOTO` `[S]` · 0140 vistas `rpt.*` · 0150 funciones y procedimientos · 0900 roles de BD · `siempre/9000` RLS · 1000 `saludips.*` (generado con `python3 database/generar-saludips.py`) · 1010 semillas auditor · 1020 ampliación `fact.HallazgoTarifario` · 1030 prompt del asistente.
- Nombres a respetar: `ia.PlanComercial` (no `ia.Plan`: palabra reservada); colación `Modern_Spanish_CI_AS`; tablas temporales con `COLLATE DATABASE_DEFAULT`.
- `etl.Carga` estados: `ABIERTA`, `CERRADA`, `PROCESANDO`, `OK`, `ERROR`, `RECHAZADA` (atómica, sin `PARCIAL`). `etl.Trabajo` estados: `PENDIENTE`, `EN_PROCESO`, `OK`, `ERROR`, `VENENO`; `EmpresaId` NULL = trabajo global; `UsuarioId` = contexto del solicitante.
- Azure SQL gratuito (General Purpose serverless): admite RLS y columnstore; límite 32 GB y cupo vCore-s: medir en pilotos y configurar pausa al agotar.

## 5. API y procesamiento
**`IAnalitica.Api` (`/v1`)**: `POST /connect/token` · `POST /v1/cargas` · `POST /v1/cargas/{id}/lotes/{n}` · `POST /v1/cargas/{id}/cerrar` · `GET /v1/cargas/{id}` · `GET /v1/cargas/{id}/rechazos` · `GET /v1/estado` · `GET /v1/regulacion/precios-maximos?desde=` (bajada; **aún no implementada**, F4 pendiente) · `POST /v1/prospectos` (anónimo, honeypot, rate limit). Un solo flujo de cargas para todas las entidades. Errores Problem Details RFC 9457 (códigos en spec 05 §7). OpenIddict responde `invalid_client` con 401. El cuerpo nunca se registra.
**Portal (`/q`)**: `/q/dashboards/{codigo}`, `/q/widgets/{codigo}/datos`, `/q/reportes`, `/q/hallazgos`, `/q/asistente/...`, `/q/admin/*`. Páginas: `/costos`, `/asistente`, `/auditor`, `/precios`, `/admin/regulacion`, «Mis reportes».

### Ingesta (implementado en F2)
- **Entidades aceptadas:** 16 catálogos, `asientos`, `facturas` (VENTANA, INICIAL, INCREMENTAL para anulaciones = entidad `facturas-anulaciones` en `/v1/estado`) y `configuracion` (FOTO, F4). Todo lo demás: `400 ENTIDAD_NO_SOPORTADA`.
- Carga duplicada = mismo hash (entidad, modo, ventana/corte, contenido de lotes) → `409 CARGA_DUPLICADA` al cerrar; cuenta para la marca de agua. Cerrar es idempotente.
- `sumaControl` obligatoria en `asientos` (Σ débito + Σ crédito) y `facturas` (Σ `valorNeto`, anuladas incluidas); tolerancia 0,01; sin ella `422`.
- Fila rechazada conserva su versión anterior; comprobante con alguna línea rechazada se rechaza completo (`COMPROBANTE_INCOMPLETO`); fila fuera de ventana → `FUERA_DE_VENTANA`; referencia requerida inexistente → `REFERENCIA_INEXISTENTE:<campo>`, opcional → `NULL`.
- Tercero paciente con `nombre` invalida toda la carga (`422 VALIDACION_NEGOCIO`). COMPLETA marca inactivas solo las filas ausentes en tablas con bandera de activo.
- Transacción única por carga (atómica) con `XACT_ABORT`; serialización por empresa y entidad con applock (ocupado → pospone 30 s sin consumir intento). Staging se borra en `OK`, se conserva en `ERROR`. Errores guardan solo tipo de excepción/número SQL.
- Periodo cerrado → `409 PERIODO_CERRADO` (consulta `etl.PeriodoCierre`; **nada lo cierra todavía**).
- Cola: `etl.Trabajo` (cola SQL); clave de exclusión `EMP:n|CARGA:id`; reintentos 1/2/4/8/16 min, `MaxIntentos` 5 → `VENENO`; prioridades: catálogos 3, asientos 5, facturas 6, cartera 7, auditor/precios 6, costos 8. Worker: máx. 2 trabajos simultáneos (configurable). `DisponibleEn` es `datetime2(0)`: la toma admite 1 s de holgura. `LimitePorMinuto` por defecto 60 (sugerido 600 para ingesta).
- Cadena por empresa: `PROCESAR_CARGA` → `CALCULAR_CARTERA` (corte = hoy Colombia) → `AUDITAR_CONFIG` → `VALIDAR_PRECIOS` → `CALCULAR_COSTOS` (uno por mes de la ventana, máx. 36, solo si la empresa tiene reglas) → `ALERTAS` (**no implementado**).
- Reportes y IA corren con `rol_app` y el contexto guardado en `etl.Trabajo`; cargas y cálculos con `rol_etl`.

## 6. Módulos funcionales y estado real
| Módulo / capacidad | Estado | Detalle |
|---|---|---|
| Base (F0) | ✅ | Solución, mediador, DbUp, Identity+2FA TOTP (sin QR: clave y `otpauth://`), alta de empresa y kit de entrega, gestión de usuarios, RLS, OpenTelemetry local, CI |
| Ingesta y cartera (F2) | ✅ salvo cuadre real | Lógica de cartera probada con escenarios sintéticos; falta cuadre contra `CARTERAESTADOCUENTAEDADES` (≤ 0,5 %) con datos del piloto |
| Capa semántica y dashboards (F3) | ✅ parcial | Métricas en código; motor SQL desde vistas `rpt.*` (lista blanca); caché por empresa+métrica+filtros+versión de carga. Dashboards de fábrica versionados en código (editor y `cfg.Dashboard` = fase 2) |
| Reportes en segundo plano (F3) | ✅ | PDF (QuestPDF), Excel, CSV vía `rpt.SolicitudReporte` + `etl.Trabajo`; 5 en cola por usuario, 1 a la vez; reutiliza idéntico del mismo usuario en 24 h; caducan a 7 días; descarga por el portal con sesión (disco local, **no** SAS/Blob) y auditada en `aud.Acceso`; CSV neutraliza `= + - @` |
| Auditor de configuración (F4) | ✅ | 10 reglas: CON-01, CON-02, CON-04, FE-01, FE-02, FE-03, TAR-01, TAR-02, TAR-06, USU-01 (marcadas `PorVerificar = 1` si dependen de `[S]`) |
| Validador de precios (F4) | ✅ parcial | R1, R2, R5, R6. Importador solo JSON con mapeo configurable `Regulacion:PreciosMaximos:Mapeo` (sin mapeo no importa). Ningún hallazgo crítico sin factor de presentación validado |
| Motor de costos (F5) | ✅ | `etl.usp_CalcularBasesCosto` + `etl.usp_DistribuirCostos` (escalonado, conservación del costo, `THROW 50010` si no cuadra); reglas por portal (`/costos`, máx. 24 meses por recálculo, auditado); dashboard «Estado de resultados con distribución de costos» (`financiero.pyg_distribuido`) |
| Asistente IA (F5) | ⚠️ sin proveedor | Orquestador, herramientas, cupo, seudonimización probados con modelo simulado; sin `IAsistenteChatClientFactory` real responde «no hay un proveedor de IA configurado» |
| Conector SaludIPS (F1) | ✅ en simulación | Repo `Juanprojas1982/SaludIPS2`, rama `feature/analitica-conector`; no probado contra una BD real |
| Landing | ✅ borrador | Repo `ianalitica-landing`, estática, sin precios («Cotizar»), textos y datos provisionales |

**Dashboards por fase (spec 14 y 09):** fase 1 — FINANCIERO (gerencial, P&G con/sin distribución, balance y razones), CICLO_INGRESO (FE/RIPS, facturado sin radicar, re-trabajo), CARTERA (antigüedad, DSO por pagador, conciliación), AUDITOR_CONFIG, AGENDA, FARMACIA, ESE_PRESUPUESTO · fase 2 — DIAGNOSTICO, NOMINA_COSTOS, INVENTARIO, GLOSAS, TESORERIA, ESE avanzado · fase 3 — HOSPITALARIO, benchmarking, pronóstico. **AGENDA, FARMACIA, ESE_PRESUPUESTO, flujo de caja, ejecución presupuestal, «dinero detenido por etapa» y «atendido no facturado» NO existen aún**: la ingesta no trae sus hechos.

### Reglas de negocio ya fijadas
- `mContratos.CodTipoContrato`: 1 Evento, 2 Capitación, 3 PGP, 4 Paquete. EPS↔tercero: `mEmpresas.CodInstitucionAdm` → `mInstituciones.CodInstitucion` → `TerceroId`. `EstadoEntidad`: 1 confirmado, 2 anulado. `DetalleCartera`: 1 CxP, 2 CxC. Los parámetros de SaludIPS se copian solos a la nueva vigencia (D15; PAR-01 queda como control).
- Balance: grupos de `dim.CuentaHomologada.GrupoBalance`; si falta, PUC comercial `[S]`: activo corriente = grupos 11–14, pasivo corriente = 21–25. Ingresos = clase 4; costos y gastos = clases 5, 6, 7; se excluyen comprobantes `CIERRE`. Balance = acumulado hasta el periodo final (correcto solo con histórico completo). ESE usa plan CGN (por validar).
- DSO por pagador = saldo CxC al corte ÷ facturado en 90 días × 90. Conciliación = cartera calculada vs saldo contable de cuentas con `DetalleCartera`.
- FE: estados `0 enviada, 1 cargada, 2 error, 3 sin enviar`; «problema» = 2 o 3; días a radicación = fecha RIPS − fecha emisión.
- Una métrica se enlaza con una sola vista; un filtro global que la vista no tiene **no se aplica y el widget lo avisa**. Periodo por omisión = últimos 12 meses (Colombia). Tablas/barras muestran 50/10 filas; el reporte exporta todo (tope 100.000 por sección).
- Auditor — estados de hallazgo: `NUEVO` → `ABIERTO` → `CORREGIDO` (automático al dejar de detectarse); `ACEPTADO` y `FALSO_POSITIVO` los pone una persona con justificación ≥ 10 caracteres y **suprimen** el hallazgo; un CORREGIDO que reincide crea hallazgo nuevo. Índice de salud: cada hallazgo abierto resta 20/10/4/1 (crítica/alta/media/baja), piso 0. El motor corre con `rol_etl` y filtra por `@EmpresaId`. Si una regla falla, sus hallazgos quedan como estaban.
- Validador — límite por unidad = `PrecioMaximoUnidad` (validado) o `PrecioMaximoPresentacion / UnidadesPorPresentacion`; factor validado si es OFICIAL o tiene `ValidadoEn`. Tarifa (`pTarifasSuministros.Valor`) y costo (`pSuministros.Costo`) se asumen por unidad de consumo `[S]`. Precios máximos = catálogo global `reg.*` (sin RLS), escribe solo `admin.catalogos`.
- Costos: bases por defecto — personal para administración, actividades para apoyo asistencial. Hoy **todo centro entra como PRODUCTIVO** (homologación `TipoOrigen`→`TipoCC` es `[S]`); solo la base INGRESO se alimenta de la ingesta; las bases manuales (m², kilos de ropa, porcentaje) no tienen carga de valores. Un centro sin destinos con base conserva su costo.

## 7. Asistente IA
- Modelos: Haiku 5.5 en pruebas, Sonnet 5.5 en producción (los nombres salen de `ia.Modelo`; precios del script 0100 a 2026-10-06, **verificar antes de producción**). **Sin proveedores chinos** (D20).
- Herramientas = `listar_metricas` y `consultar_metrica` sobre el motor semántico (hereda RLS, permiso `dash.*` y módulo activo; máx. 200 filas). Las diez herramientas específicas de spec 07 §4 quedan para cuando existan sus métricas.
- Bucle de herramientas escrito a mano (no `FunctionInvokingChatClient`) para reservar, medir y cortar por iteración. Reserva = estimación 3 caracteres/token + 1.500 tokens de herramientas × iteraciones máximas; `ia.usp_ReservarCuota` → bucle → `ia.usp_LiquidarReserva`; valoración con `ia.fn_TarifaVigente`; una fila `ia.UsoLlamada` por iteración. `CUPO_AGOTADO` no llama al proveedor.
- Sin fila en `ia.Tarifa` responde «falta configurar la tarifa». `ia.usp_CerrarPeriodo` existe pero nadie lo llama. Plan por defecto `PILOTO` con cupo 0 `[S]`.
- Seudonimización: todo valor de dimensiones `usuario` y `tercero` viaja como «Usuario #id» y se re-hidrata solo para la empresa; el historial guardado conserva el alias; si `GuardarTextoConversacion = 0` no hay memoria entre turnos. La salida pierde HTML y enlaces externos.
- `ia.EmpresaConfig.VisibilidadCosto = 'ADMIN'` por defecto (lo ve quien tenga `admin.usuarios`). `ia.RolPermitido` no se aplica (basta `ia.usar`). Fase 1 = solo pruebas internas; clientes en fase 2.
- Cobro (D21): cupo incluido en el canon + excedente a tarifa por token configurable con tope autorizado.

## 8. Infraestructura y calidad (D19, D32)
- Hosting **Azure confirmado; región pendiente** (concepto del abogado; candidatas Brazil South, Chile Central, Mexico Central, East US 2; verificar disponibilidad de Azure SQL). Pruebas: Azure SQL gratuito con pausa, Container Apps por consumo (Api, Web, Worker; escala a cero), Blob LRS, Key Vault, Log Analytics en cupo gratuito, presupuesto USD 20 con alertas 50/80/100 %. Producción solo con cotización aprobada.
- Despliegue: GitHub Actions compilar → pruebas → migrador DbUp → desplegar; ambientes `dev` y `prod` (aprobación manual para `prod`).
- Observabilidad: OpenTelemetry → Azure Monitor (muestreo: 100 % de spans con error, fracción de los exitosos, **por span** no por traza); `traceparent` desde el conector; auditoría de negocio en `aud.*` (solo inserción).
- Pruebas: 398 al cierre de F5 (60 dominio, 90 aplicación, 253 integración) reportadas en verde; no re-ejecutadas en la sesión de consolidación.
  ```bash
  dotnet build && dotnet test
  dotnet test tests/IAnalitica.Integration.Tests --filter "FullyQualifiedName~Costos|FullyQualifiedName~Asistente"
  ```

## 9. Plan por fases
| Fase | Estado | Criterio de aceptación | ¿Cerrado con datos reales? |
|---|---|---|---|
| F0 Base | ✅ | 2 empresas; `Admin{NIT}` de A no ve B | Sí (prueba automatizada) |
| F1 Conector | ✅ simulación | JSON cuadran contra `SIPS_5` y `ESTADORESULTADOSCENTROCOSTOHORIZONTAL` | No |
| F2 Ingesta | ✅ | 13 meses; cartera ≤ 0,5 % vs `CARTERAESTADOCUENTAEDADES` | No |
| F3 Dashboards | ✅ parcial | Los 3 pilotos los usan | No |
| F4 Auditor y validador | ✅ parcial | ≥ 80 % de hallazgos confirmados reales | No |
| F5 Costos e IA | ✅ parcial | Conservación del costo; consumo IA dentro del tope | Conservación y tope: sí (sintético). Pilotos y modelo real: no |
| F6 Despliegue piloto | ⏳ | 95 % de noches con carga correcta durante 30 días | — (bloqueada por P1 y por datos reales) |

## 10. Pendientes y decisiones abiertas
### Técnicos (próximos)
1. Adaptador Anthropic (`IAsistenteChatClientFactory`): instalar y probar el paquete `Anthropic`; requiere clave y aprobación D32.
2. Ingesta de `nomina`, `prestaciones`, `movimientos-inventario` (bases COSTO_PERSONAL, ACTIVIDADES, ATENCIONES, CONSUMO_INSUMOS) y de los hechos de agenda, farmacia, tesorería (`recibos`), presupuesto, admisiones/prestaciones. Hoy se rechazan con `ENTIDAD_NO_SOPORTADA`.
3. Clasificación de centros de costo (`TipoCC`) y carga de bases manuales de costos.
4. Cierre de periodo (`etl.PeriodoCierre`) y trabajo `ALERTAS`; alerta para `VENENO`; llamar `ia.usp_CerrarPeriodo`.
5. Bajada del catálogo regulatorio a SaludIPS y validación en tiempo real; reglas R3, R4, R7–R9; descarga automática del dataset `nauz-qkjw` (campos por validar).
6. Resto del auditor (CON-03, CON-05…09, TAR-03…05/07/08, FE-04/05, AGE, ALM, PAR, USU-02/03); parte CxP de CON-04; resumen semanal por correo (sin proveedor de correo, D32).
7. Asistente: streaming SSE, panel `Ctrl+K`, valoración 👍/👎, evaluaciones (`ia.Modelo.EvaluacionAprobada`), caché de prompt, conteo exacto de tokens, resumen de historial, `ModeloRespaldoId`, consumo visible y alertas 70/90 %.
8. Contrato 05: agregar `tarifas-suministros`, `catalogo-medicamentos`, `ordenes-externas`, `presupuesto-publico`, `contratos-publicos` con tablas destino (P9). Compartir DTO `Ingesta.Contracts` con el conector (NuGet privado vs copia verificada por pruebas).
9. Selector de empresa del superadmin con motivo y registro `aud.AccesoSuperadmin` (no existe); dashboards del superadmin.
10. Almacenamiento de reportes compartido o Blob si hay varios nodos (`Reportes:Almacen:Ruta`); llaves de Data Protection y de firma de token en Key Vault (F6).
11. Gráficos en PDF (hoy tablas y KPI); logo en PDF; QR del 2FA.

### Decisiones de diseño abiertas
- **Render:** `InteractiveAuto` + MudBlazor (spec original) vs. SSR+SVG actual (F3). Decidir si se quiere interactividad (requiere proyecto `IAnalitica.Web.Client` no previsto en la estructura).
- Usuarios únicos globales vs. sufijo por empresa.
- Transacción única por carga vs. partir por día si la carga inicial de ~2 M líneas lo exige.
- Dashboard gerencial propio vs. widgets de otros módulos (`ModulosAdicionales`).
- Exportador de Azure Monitor y su límite de ingesta; licencias de Mapperly y ApexCharts.
- Permiso propio `costos.configurar`.

### Para el dueño / equipo funcional
| # | Pendiente | Responsable | Bloquea |
|---|---|---|---|
| P1 | Región de Azure | Dueño + abogado | F6 |
| P2 | Validar `[S]` de 06, 11 y 15 (fechas de atención, `VrCosto`, `DiaHorario`, `TipoPersona`, `ClaseTercero`, `TipoCentroCosto`, `EstadoEntidad = 0`, factores por CUM) | Equipo funcional | Métricas que los usan |
| P3 | Cobertura de `dFacturasServicios` | Técnico | Enlace de facturas (hay respaldo) |
| P5 | Revisión y firma de documentos legales | Abogado | Contratos y pilotos |
| P6 | Fórmulas de riesgo de la ESE | ESE piloto | Simulador (fase 2) |
| P7 | Reglas de distribución de costos por piloto (centros, orden, base) | Cada piloto | Cierre de F5 |
| P8 | Dominio y marca | Dueño | Landing |
| P10 | Confirmar matriz rol × permiso | Dueño | Nada (script nuevo) |
| P11 | Planes de IA, tarifa de venta (COP por millón de tokens), cupo, autorización de excedente, visibilidad del costo | Dueño | IA a clientes |
| — | ¿`CodPlan` es parte del cruce de tarifas? ¿`pSuministros.Costo` y `Valor` son por unidad o por presentación? ¿Tabla de CxP para CON-04? ¿Ruta del menú SaludIPS por hallazgo? ¿Campos del dataset `nauz-qkjw`? | Equipo funcional | TAR-02, validador, CON-04 |
| — | Homologación `TipoOrigen` → administrativo/apoyo/productivo | Equipo funcional | Reglas por defecto de costos |
| — | Diagnóstico SIPS_108 vs SIPS_550_1: ejecutar el script de §4 de `30_DIAGNOSTICO_SIPS108_vs_SIPS550.md` contra una base real; causa probable (sin confirmar) = INNER JOIN a la tarifa del manual | Técnico | — |

## 11. Riesgos
| Riesgo | Control |
|---|---|
| Cifras que no cuadran | Cuadre contra reportes de SaludIPS antes de mostrar un dashboard a un cliente |
| Falsos positivos en auditor/validador | Reglas de alta certeza, `FALSO_POSITIVO`, factores validados |
| Fuga entre empresas | RLS + filtro en handlers + pruebas por PR |
| Costo de nube o IA | D32, presupuesto con alertas, topes de IA, cupo gratuito con pausa |
| Licencias comerciales | Verificar al instalar; QuestPDF revisión anual (umbral USD 1 M) |
| Alcance creciente | Lo nuevo entra como fase posterior |
| Ramas largas sin integrar | PR #7 `feature/f5-costos-ia` → `main` |

## 12. Decisiones (registro)
| # | Decisión |
|---|---|
| D01 | Producto IAnalitica, solo sector salud |
| D02 | Paga la institución, cada una por su cuenta |
| D03 | La implementación incluye configuración, cuadre, reglas y capacitación |
| D04 | 3 pilotos: operador farmacéutico, IPS privada, ESE |
| D05 | Landing sin precios (solo «Cotizar»), independiente del HIS; caso SaludIPS sin cifras de mejora hasta medir y autorizar |
| D06 | Landing en SmarterASP con dominio propio; la plataforma se aloja aparte |
| D10 | .NET 10, Clean Architecture, CQRS con mediador propio, EF Core, SQL Server, Blazor. Repo `Juanprojas1982/IAnalitica` |
| D11 | BD central multiempresa con `EmpresaId` + RLS; usuario de una sola empresa; superadmin ve todas |
| D12 | ASP.NET Core Identity (no Entra); 2FA obligatorio para administradores |
| D13 | Ingesta por API OAuth2 client credentials; en el conector «usuario/contraseña» = ClientId/ClientSecret |
| D14 | Conector dentro de SaludIPS (`SaludIPSNet.Analitica.Conector`, net48, servicio de Windows); SaludIPS es on-premise con salida a internet |
| D15 | Los parámetros de SaludIPS se copian solos a la nueva vigencia |
| D16 | Dashboards declarativos sobre capa semántica; perfiles por módulos activos |
| D17 | Auditor de configuración: solo lectura, sugiere y hace seguimiento |
| D18 | Validador de precios máximos con catálogo regulatorio central |
| D19 | Hosting Azure; región pendiente |
| D20 | Anthropic: Haiku 5.5 en pruebas, Sonnet 5.5 en producción; sin proveedores chinos |
| D21 | Cobro IA: cupo en el canon + excedente a tarifa configurable con tope |
| D22 | OpenTelemetry → Azure Monitor con muestreo; auditoría de negocio en `aud.*` |
| D23 | Cola detrás de `IColaTrabajos`; pilotos con cola SQL; Storage Queues solo con cotización |
| D24 | Reportes imprimibles/descargables siempre en segundo plano |
| D25–D27 | Solo el superadmin crea empresas; contraseña inicial aleatoria de 14 caracteres (72 h, cambio y 2FA obligatorios); un solo `Admin{NIT}` por empresa |
| D30 | Diseño con Opus, implementación con Sonnet |
| D31 | Documentos legales: borradores que revisa y firma un abogado |
| D32 | Costo primero: precio oficial con fecha + estimado mensual + aprobación explícita |
| D33 | Licencias comunitarias: la empresa factura < USD 1 M/año (declarado por el dueño, 2026-10-26); QuestPDF comunitaria |
| D34 | Mediador propio, sin MediatR; preferir componentes .NET o MIT/Apache sin vencimientos |
| D35 | RLS por `SESSION_CONTEXT`; desactivación controlada por la aplicación; dos roles de BD |
| D36 | RLS en script que corre en cada despliegue |
| D37 | Matriz rol × permiso `[S]` |
| D38 | Primer superadmin por comando del migrador; plan `PILOTO` con cupo 0 `[S]` |

## 13. Divergencias spec original ↔ implementación (la implementación manda)
| Spec original | Realidad |
|---|---|
| Dashboards con MudBlazor/ApexCharts, `InteractiveAuto` | SSR + estado en URL + SVG, sin JavaScript |
| Descarga de reportes por SAS de 15 min en Blob | Disco local, descarga por el portal con sesión |
| Diez herramientas fijas del asistente | Dos herramientas genéricas sobre el catálogo semántico |
| Dashboard `auditor_config` en la capa semántica | Página dedicada `/auditor` (permiso `dash.auditor_config` sí aplica) |
| Cadena nocturna incluye `DISTRIBUIR_COSTOS` y `ALERTAS` | Existe `CALCULAR_COSTOS` (por mes); `ALERTAS` no existe |
| Exportador Azure Monitor | Consola/OTLP |
| README y menciones a MediatR | No se usa MediatR |

## 14. Documentos que siguen vigentes (detalle)
Consultar solo para el tema indicado; este documento prevalece en caso de conflicto.
| Archivo | Tema |
|---|---|
| `02_mapeo_SaludIPS.md`, `03_analisis_reportes_SaludIPS.md` | Reglas de negocio y reportes de SaludIPS (su afirmación sobre presupuesto público la reemplaza `17_`) |
| `05_contrato_api_ingesta.md` | Contrato de ingesta (entidades, campos, modos, errores §7, §6b) |
| `06_extraccion_saludips.sql`, `11_extraccion_operativa_saludips.sql` | Extracción en la BD de SaludIPS (funciones `analitica.fn_*`) |
| `07_spec_asistente_ia.md` | Asistente IA (usar `ia.PlanComercial`) |
| `08_consentimiento_proveedor_ia.md`, `legal/` | Borradores para el abogado (no son contexto técnico) |
| `09_catalogo_kpi_gerencial.md` | Catálogo de KPI |
| `12_spec_conector_saludips.md` | Conector (net48, parámetros `Analitica.*`, modo simulación) |
| `13_spec_landing_page.md` | Landing |
| `14_spec_capa_semantica_dashboards.md` | Métricas y dashboards |
| `15_spec_gestor_farmaceutico_y_validador_precios.md` | Farmacia y validador |
| `16_spec_auditor_configuracion_saludips.md` | Auditor |
| `17_spec_ese_sector_publico.md` | ESE |
| `18_infraestructura.md`, `19_spec_observabilidad.md`, `20_spec_colas_concurrencia_rendimiento.md`, `21_spec_reportes_en_segundo_plano.md`, `22_spec_alta_empresa_y_usuarios.md` | Infra, observabilidad, colas, reportes, alta de empresa. Sus bloques SQL y lo que §3 cambia de la RLS están reemplazados |
| `30_DIAGNOSTICO_SIPS108_vs_SIPS550.md` | Análisis de diferencias entre reportes de facturación |
| `fuentes-saludips/` | Insumos (CSV de reportes) |
| `archivo/` | **Histórico, no usar**: spec maestra original, notas F0–F5 y DDL reemplazado |
