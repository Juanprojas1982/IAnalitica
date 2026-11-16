# PL-17 · Auditor de configuración, fase 2

Estado: diseñado, no implementado. Módulo `AUDITOR_CONFIG`. Script `1190_auditor_fase2.sql`. Cierra el pendiente técnico 6 (`06` §10).
Preparación (2026-11-16): **lotes 1 y 3 listos para implementar.** El resto, tras medir falsos positivos con el piloto.
Es el diferenciador que ya funciona: ampliarlo es lo que menos depende de datos nuevos (la foto `configuracion` ya llega).

## Objetivo
Pasar de 10 a las 37 reglas del catálogo (`03` §21), agregar el resumen semanal y la campaña de cierre de año, y dar al fabricante la vista de errores repetidos entre clientes.

## Situación actual
10 reglas implementadas (CON-01, CON-02, CON-04 parte CxC, FE-01, FE-02, FE-03, TAR-01, TAR-02, TAR-06, USU-01). Cada regla es una clase (`IReglaConfiguracion`), con metadatos en `cfg.ReglaConfiguracion`; hallazgos en `fact.HallazgoConfiguracion`; índice de salud calculado.

## Reglas por lote (cada lote = un PR pequeño)
Orden por certeza: primero las que no dependen de un `[S]`.

| Lote | Reglas | Depende de |
|---|---|---|
| 1 · Contabilidad | CON-03 cuenta inexistente o de clase incoherente · CON-05 comprobante, forma de pago, caja o banco sin cuenta · CON-06 movimientos sin centro en cuenta que lo exige · CON-07 meses sin cierre · CON-08 artículos o grupos sin configuración contable · CON-09 nómina sin cuenta o sin configuración anual · CON-04 parte CxP | «¿Tabla de CxP para CON-04?» sigue abierta (`06` §10): esa parte queda `PorVerificar` |
| 2 · Tarifas y contratos | TAR-03 tarifa en 0, negativa o con salto > X % · TAR-05 contrato vencido con facturación, sin sedes o tope > 90 % · TAR-07 procedimiento sin centro de costo · TAR-08 pagador sin tercero | TAR-04 **no** se duplica: es R1/R2 del validador; se enlaza |
| 3 · Factura electrónica | FE-04 dígito de verificación inválido (módulo 11, en C#; ya existe el cálculo para el alta de empresas: reutilizar) o sin correo · FE-05 terceros duplicados | — |
| 4 · Agenda | AGE-01 horarios solapados (C#) · AGE-02 médico inactivo con agenda · AGE-03 minutos o cupos fuera de rango · AGE-04 festivos sin cargar (desde diciembre) · AGE-05 médico sin tercero ni registro | `DiaHorario` es `[S]` (P2) → AGE-01 `PorVerificar` hasta confirmarlo |
| 5 · Almacén | ALM-01 medicamento sin registro INVIMA o vencido · ALM-02 lotes · ALM-03 mínimo > máximo · ALM-04 marca de regulado vs. lista oficial | ALM-04 comparte lógica con R7 (PL-13) |
| 6 · Parámetros y usuarios | PAR-01 parámetros ausentes en la nueva vigencia · PAR-02 configuración anual sin el año · USU-02 segregación de funciones · USU-03 usuarios genéricos | USU-02 necesita el significado de los permisos de SaludIPS (`[S]`): matriz de pares incompatibles como dato editable, no en código |

Para cada regla: clase en `IAnalitica.Modulos.Auditor`, semilla en `cfg.ReglaConfiguracion` (script nuevo; nunca editar `1010`), severidad y umbrales editables, texto «por qué importa» y «dónde corregir» (**la ruta del menú de SaludIPS es un pendiente funcional**: dejar el campo vacío antes que inventarla), evidencia de impacto en 30 días cuando exista el hecho.
Reglas con significado sin confirmar nacen con `PorVerificar = 1` y **no restan** al índice de salud hasta confirmarse (verificar que el cálculo actual ya lo hace; si no, corregirlo en este plan).

## Funciones nuevas
### Resumen semanal
- Trabajo `RESUMEN_AUDITOR` semanal por empresa: hallazgos nuevos, corregidos y abiertos por severidad, índice y su variación.
- Entrega: **notificación en el portal** (campana) con enlace a `/auditor`. Por correo solo cuando exista proveedor (P15, D32); el envío reutiliza `ENVIAR_CORREO` de facturación (`08`), sin datos de personas en el cuerpo.

### Campaña de cierre de año
- Del 1-nov al 31-ene, `/auditor` muestra un bloque «Cierre de vigencia» con el avance de CON-02, PAR-01, PAR-02, AGE-04, FE-01 y ESE-05 (PL-14): cuántas faltan y cuáles. Es una vista filtrada, no un motor nuevo.

### Vista del fabricante (superadmin)
- `Admin/AuditorConsolidado.razor`: por regla, en cuántas empresas falla, cuántos hallazgos, cuántos marcados como falso positivo (calibra la regla) y tiempo medio de corrección.
- Requiere leer `fact.HallazgoConfiguracion` de todas las empresas: lo permite `EsSuperAdmin = 1` en la RLS. **Pendiente previo:** el selector de empresa del superadmin con motivo y registro en `aud.AccesoSuperadmin` no existe (pendiente 9). Esta vista es agregada y sin datos de una empresa en particular: registrar el acceso como consulta global. No mostrar entidades (nombres de pagadores, usuarios) de los clientes: solo conteos.

### Exportación
Excel de hallazgos ya existe por reportes; agregar las columnas de las reglas nuevas sin cambiar el formato.

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1190_auditor_fase2.sql` (semillas; uno por lote si se prefiere: 1190–1195) |
| Módulo | `Modulos.Auditor/Reglas.cs` (o un archivo por área), `ReglaSql.cs` |
| Application | `Auditoria/*` (resumen, consolidado) |
| Infrastructure | manejador `RESUMEN_AUDITOR`, `DependencyInjection.cs` |
| Web | `Components/Pages/Auditor.razor`, `Admin/AuditorConsolidado.razor` |
| Pruebas | `AuditorTests.cs` (un caso positivo y uno negativo por regla), `AuditorWebTests.cs`, aislamiento |

Símbolos para `impact`: `IReglaConfiguracion`, el motor que ejecuta las reglas, `ManejadorAuditarConfig`, el cálculo del índice de salud.

## Pruebas
- Por regla: configuración que la dispara, configuración sana, y reincidencia (CORREGIDO → hallazgo nuevo).
- Una regla que lanza excepción no altera los hallazgos de las demás (comportamiento actual; no romperlo).
- `PorVerificar` no resta al índice.
- Consolidado: solo superadmin; sin nombres de entidades de clientes.
- Aislamiento: hallazgos de A invisibles para B.

## Riesgos
- **Falsos positivos**: el criterio de aceptación de F4 es ≥ 80 % de hallazgos confirmados reales, y aún no se ha medido con un piloto. Meter 27 reglas más antes de medir las 10 actuales multiplica el ruido. Recomendación: lotes 1 y 3 primero (alta certeza), medir con el piloto y seguir.
- Versiones de SaludIPS: cada regla declara `VersionMinimaSaludIPS`; tablas que no existan en una versión antigua no deben tumbar la auditoría.
- Lista blanca de extracción: reglas que necesiten una tabla nueva obligan a ampliar la foto y regenerar `saludips.*` (script nuevo, conector nuevo).
