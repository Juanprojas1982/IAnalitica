# Notas de implementación — F4 · Auditor de configuración y validador de precios máximos

Complementa a `24_SPEC_MAESTRA.md`, `16_spec_auditor_configuracion_saludips.md` y `15_spec_gestor_farmaceutico_y_validador_precios.md` (mandan). Aquí: qué se implementó, qué quedó fuera, supuestos `[S]` y decisiones pendientes.

## 1. Qué incluye F4
| Entregable | Dónde | Cómo se verifica |
|---|---|---|
| Copia tipada de la configuración (52 tablas) | `database/scripts/1000_saludips_configuracion.sql`, **generado** por `database/generar-saludips.py` desde `docs/fuentes-saludips/diccionario_columnas.csv` + `database/Herramientas/configuracion_columnas.tsv` (las columnas que el conector envía, extraídas de `A40_fn_Configuracion.sql`) | El script corre en cada prueba de integración; «52 tablas; 0 sin PK» |
| Entidad `configuracion` (FOTO) | `Infrastructure/Ingesta/ProcesadorConfiguracion.cs`; `CatalogoEntidades` | `ConfiguracionTests`: lista blanca = tablas que existen en `saludips`; tabla desconocida o fila sin llave → rechazo; la foto nueva reemplaza la anterior y no toca a otras empresas |
| Motor del auditor | `Domain/Auditoria` (contratos), `Infrastructure/Auditoria/MotorAuditoria.cs` | `AuditorTests` (ciclo de vida, ajustes por empresa, regla que falla, índice, job) |
| 10 reglas: CON-01, CON-02, CON-04, FE-01, FE-02, FE-03, TAR-01, TAR-02, TAR-06, USU-01 | `Modulos.Auditor/Reglas.cs` (SQL sobre `saludips.*`); semillas y textos en `database/scripts/1010_auditor.sql` | `AuditorTests.Detecta_los_problemas_…` con una foto sintética con aciertos y falsos negativos |
| Índice de salud 0–100 por área y total, con historial | `fact.IndiceSaludConfiguracion` (1010) | `AuditorTests.Calcula_el_indice…` |
| Página «Salud de la configuración» (`/auditor`) | `Web/Components/Pages/Auditor.razor`, `Application/Auditoria` | `AuditorWebTests` (HTML escapado, acciones, módulo inactivo) |
| Aceptar / falso positivo (con justificación) / reabrir | `CambiarEstadoHallazgoCommand`; deja rastro en `aud.CambioAdministrativo` | `AuditorWebTests`, `AuditorTests` (Application) |
| Validador de precios máximos R1, R2, R5, R6 | `Infrastructure/Auditoria/ValidadorPrecios.cs`; script `1020_validador_precios.sql` | `ValidadorPreciosTests` (unidades, por verificar, ciclo de vida) |
| Página «Cumplimiento de precios» (`/precios`) | `Web/Components/Pages/Precios.razor` | `PreciosWebTests` |
| Catálogo regulatorio: importar lista de precios máximos y validar factores | `Application/Regulacion`, `Infrastructure/Regulacion`, página `/admin/regulacion` (solo `admin.catalogos`) | `RegulacionTests`, `RegulacionWebTests` |
| Trabajos `AUDITAR_CONFIG` y `VALIDAR_PRECIOS` | Se encolan al cargar la foto (prioridad 6, exclusión por empresa); los ejecuta el worker | `AuditorTests.Cargar_la_foto_encola…`, `ValidadorPreciosTests.Cargar_la_foto_encola…` |

Suite completa al cierre: 356 pruebas (60 dominio, 75 aplicación, 221 integración) en verde.

## 2. Lo que NO se entregó (y por qué)
| Pendiente | Motivo |
|---|---|
| Resumen semanal por correo | No hay proveedor de correo (costo, D32). Los datos para armarlo ya existen (`fact.HallazgoConfiguracion`, estados y fechas) |
| Resto del catálogo de reglas (CON-03, CON-05…09, TAR-03…05/07/08, FE-04/05, AGE, ALM, PAR, USU-02/03) | La spec 16 §7 pide empezar con las 10 de mayor certeza. Cada una es una clase nueva; el motor no cambia |
| Campaña de cierre de año y vista consolidada del fabricante | Spec 16 §8 paso 5 (fase siguiente) |
| Reglas R3, R4, R7, R8, R9 del validador | Spec 15 §6 prioriza R1, R2, R6, R5. R3/R4 necesitan unidades y compras; R7/R8/R9 dependen de decisiones regulatorias y tributarias |
| Descarga automática del dataset `nauz-qkjw` de datos.gov.co | El sandbox no llega a datos.gov.co y los campos del dataset no están verificados (spec 15 §1 lo marca «por validar»). El importador recibe el archivo JSON y el **mapeo de campos se configura** (`Regulacion:PreciosMaximos:Mapeo`), sin valores por defecto: sin mapeo no importa |
| Bajada del catálogo a SaludIPS (`GET /v1/regulacion/precios-maximos`) y validación en tiempo real al facturar | Spec 15 §6 pasos 3–4; depende del conector y de SaludIPS |
| Registro INVIMA vencido en R6 por producto de `pSuministros` | El enlace `pSuministros` ↔ `Almacen.ArticulosBase` se hace por `CodigoCUM`; no hay una llave directa confirmada |
| Dashboard del auditor como dashboard de la capa semántica (`auditor_config`) | Se hizo una página dedicada (acciones sobre hallazgos no encajan en widgets de solo lectura). El permiso `dash.auditor_config` y el módulo `AUDITOR_CONFIG` sí se usan |

**Criterio de aceptación de F4** (spec 24): «el auditor detecta los errores reales de los pilotos» — no se puede cerrar aquí; requiere la foto de configuración de una instalación real.

## 3. Supuestos `[S]` por verificar
Todas las reglas dependientes de estos supuestos están marcadas `PorVerificar = 1` en `cfg.ReglaConfiguracion` y la página muestra la insignia «por verificar».
1. **Activo** = `EstadoEntidad = 1` (igual que el extractor de pagadores, `docs/06`).
2. **CON-01**: basta una fila de `mEmpresasCuentasContables` de la vigencia con `CuentaPorCobrarId`.
3. **CON-02**: cubre pagadores, comprobantes con cuentas este año y no el próximo, y `cConfiguracionAnual`. Faltan formas de pago, almacén, nómina y `SIIFNet.Parametros`. Crítica desde diciembre; los meses son umbrales (`DesdeMes`, `CriticaDesdeMes`).
4. **CON-04**: se revisan la CxC normal y la radicada de pagadores activos. La parte de CxP (`DetalleCartera = 1`) no se implementó: no se sabe qué tabla referencia esas cuentas.
5. **FE-01/FE-02**: solo se evalúa la resolución más reciente por prefijo y modalidad (una vencida pero reemplazada no es hallazgo). `NumeracionActual` = último número usado. `TipoResolucionId` no se interpreta.
6. **FE-03**: una sede «factura» si tuvo facturas en los últimos 30 días en `fact.FacturaLinea`; el `SedeId` de SaludIPS es el código de origen de `dim.Sede`. No se cruza el tipo de comprobante (los hechos no lo traen).
7. **TAR-01**: `CodManual` nulo o 0 significa «sin manual».
8. **TAR-02**: el cruce con la tarifa es solo por `CodManual`. `CodPlan` **no** es parte de la llave de `pTarifasProcedimientos`/`pTarifasSuministros` y su significado no está confirmado (la spec dice «manual y plan»).
9. **USU-01**: «retirado» = tiene contratos y todos tienen `FechaSalida` pasada; «activo» = `Bloqueado` distinto de 1 y sin bloqueo temporal vigente.
10. **Índice de salud**: cada hallazgo abierto resta 20 / 10 / 4 / 1 puntos (crítica / alta / media / baja), con piso en 0, por área y en total.
11. **Validador de precios**: la tarifa (`pTarifasSuministros.Valor`) y el costo (`pSuministros.Costo`) están por **unidad de consumo**; el pagador toma la tarifa de su manual. Límite por unidad = `PrecioMaximoUnidad` si la fuente lo trae (se considera validado) o `PrecioMaximoPresentacion / UnidadesPorPresentacion`. El factor es validado si es OFICIAL o tiene `ValidadoEn`. **Sin factor validado, R1/R2 bajan a MEDIA y se marcan «por verificar»**; sin ningún límite comparable no se marca nada (el resumen del trabajo cuenta cuántos suministros quedaron sin comparar).
12. **Fecha de hoy** = hora de Colombia (UTC−5 fijo).

## 4. Decisiones de diseño tomadas (reportar si no convienen)
- **Tablas tipadas, no JSON genérico**: sigue la intención del DDL 0090 («se generan en F4 desde el diccionario»). El precio es que un cambio de columnas del conector exige regenerar el script 1000 (`python3 database/generar-saludips.py`) y un script nuevo de migración. Todas las columnas se cargan como texto y se convierten por el tipo de la columna: un valor que no convierte falla la carga completa (atomicidad por carga).
- **El motor corre con `rol_etl` (sin RLS)**: cada regla filtra por `@EmpresaId`, que el motor siempre fija. Las consultas son constantes del código.
- **Hallazgos**: NUEVO en la primera detección, ABIERTO en la siguiente en que sigue, CORREGIDO cuando deja de detectarse (automático). ACEPTADO y FALSO_POSITIVO los pone una persona con justificación (≥ 10 caracteres) y **suprimen** el hallazgo: no se vuelve a crear. Si un CORREGIDO reincide, nace un hallazgo nuevo. Si una regla falla, sus hallazgos quedan como estaban.
- **`fact.HallazgoTarifario` se amplió** (script 1020) con `ClaveEntidad`, `NombreEntidad`, `Detalle`, `Justificacion` y `UltimaDeteccion` para poder deduplicar y explicar; la spec 15 §2.4 no los tenía. R1/R2/R5 se identifican por «suministro|pagador».
- **Cambio de estado**: primero se actualiza el hallazgo y luego se escribe `aud.CambioAdministrativo` (no comparten transacción: el repositorio usa Dapper y la auditoría EF Core). Si la auditoría fallara, el hallazgo quedaría cambiado sin rastro; es un riesgo bajo y conocido.
- **Importador de precios**: solo JSON (arreglo de objetos, la exportación de Socrata); los números deben venir con punto decimal y sin separador de miles; cualquier otro formato se rechaza, no se adivina. Una fila mala se cuenta como rechazada y no detiene el lote.
- **Los precios máximos se importan como catálogo global** (`reg.*`, sin `EmpresaId`, sin RLS). Cualquier usuario de la aplicación podría escribirlas en SQL, pero solo `admin.catalogos` (superadmin) llega al comando.

## 5. Cómo probar
```bash
dotnet test                                                            # 356 pruebas; las de integración usan SQL Server en contenedor
dotnet test tests/IAnalitica.Integration.Tests --filter "FullyQualifiedName~Auditor|FullyQualifiedName~ValidadorPrecios|FullyQualifiedName~Regulacion"
python3 database/generar-saludips.py                                   # regenera el script 1000 (no debe cambiar nada si el conector no cambió)
```
Manual: con el conector (rama `feature/analitica-conector` de SaludIPS2) enviar `configuracion` (FOTO); el worker procesa la carga y encola `AUDITAR_CONFIG` y `VALIDAR_PRECIOS`. Entrar a `/auditor` (módulo `AUDITOR_CONFIG`) y `/precios` (módulo `FARMACIA`); como superadmin, `/admin/regulacion`.

## 6. Ambigüedades para el equipo funcional
1. ¿`CodPlan` forma parte del cruce de tarifas por pagador? (cambia TAR-02 y el validador).
2. ¿`pSuministros.Costo` y `pTarifasSuministros.Valor` están por unidad de consumo o por presentación?
3. ¿Qué campos trae hoy el dataset `nauz-qkjw` y con qué formato numérico? (para fijar el mapeo por defecto).
4. ¿Qué tabla referencia las cuentas por pagar para CON-04 (`DetalleCartera = 1`)?
5. ¿La ruta del menú de SaludIPS para corregir cada hallazgo? Solo se conoce la de CON-01; el resto de `RutaSaludIPS` está vacío.
