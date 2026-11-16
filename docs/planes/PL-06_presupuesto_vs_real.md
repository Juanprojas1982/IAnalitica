# PL-06 · Presupuesto vs. real

Estado: diseñado, no implementado. Módulo `FINANCIERO`. Script `1090_presupuesto.sql`.
Preparación (2026-11-16): **listo para implementar.** DP-4 (D56: `admin.usuarios`) y DP-6 (D58: CSV) aprobadas.
Alcance: presupuesto **gerencial** por cuenta contable. El presupuesto público de la ESE (CDP, RP, obligaciones) es otro modelo (`03` §22) y no entra aquí.

## Objetivo
Comparar lo ejecutado contra lo presupuestado por cuenta, centro de costo y sede: desviación en pesos y en porcentaje, del mes y acumulada.

## Situación actual
- `fact.Presupuesto` y `rpt.vw_EjecucionPresupuestal` (versión `APROBADO`) existen. La vista no expone `ClasePuc` ni `Grupo`, así que no permite separar ingresos de gastos.
- La entidad `presupuesto` del contrato se rechaza. SaludIPS no tiene presupuesto gerencial: **el conector nunca lo enviará**; sin carga por portal el tablero queda vacío.

## Cambios
### 1. Entidad
`new EntidadIngesta("presupuesto", new[] { Modos.Ventana }, new[] { "valor" })`. Ventana = rango de meses que se reemplaza (normalmente el año).
`etl.usp_ProcesarPresupuesto`:
- Campos: `version*, periodo* (yyyymm), cuentaCodigo*, centroCostoCodigo, sedeCodigo, valor*`. `sourceKey` para rechazos = `version|periodo|cuenta|cc|sede`.
- `periodo` fuera de la ventana → `FUERA_DE_VENTANA`; cuenta inexistente → `REFERENCIA_INEXISTENTE:cuentaCodigo`; **centro de costo o sede informados pero inexistentes → rechazo** (aquí no se degrada a NULL: movería el presupuesto a «sin centro» en silencio); cuenta de clase distinta de 4, 5, 6, 7 → `VALOR_INVALIDO:cuentaCodigo`; duplicado por la llave → `DUPLICADO_EN_CARGA`.
- Reemplazo: borra de `fact.Presupuesto` las filas de la empresa de **las versiones presentes en la carga** con `PeriodoKey` en la ventana, e inserta. Otras versiones no se tocan.
- Signo: `ValorPresupuestado` positivo por naturaleza (igual que `Saldo` de `vw_EstadoResultados`). Documentarlo en la plantilla.
- El control de periodo cerrado de `IniciarCargaHandler` **no debe aplicar** a `presupuesto` (se presupuestan meses futuros y se corrige el pasado): declarar la entidad con `ExentaDeCierre: true` (propiedad nueva de `EntidadIngesta`, definida en el índice, «Mecanismos compartidos») y que el handler la consulte junto a `EsCatalogo`. No comparar por nombre de entidad.

### 2. Vista
`CREATE OR ALTER VIEW rpt.vw_EjecucionPresupuestal` agregando (sin quitar columnas): `ClasePuc, Grupo` (2 dígitos), `RealIngreso, PptoIngreso` (clase 4), `RealGasto, PptoGasto` (clases 5, 6, 7). Requiere unir `dim.CuentaContable` por `CuentaId`.

### 3. Métricas y dashboard (`ModuloFinancieroAnalitico`)
- `financiero.ppto_ingresos`, `financiero.real_ingresos_ppto`, `financiero.ejecucion_ingresos_pct` = `100.0*SUM(RealIngreso)/NULLIF(SUM(PptoIngreso),0)` (`KpiMeta: "EJECUCION_INGRESOS"`).
- `financiero.ppto_gastos`, `financiero.real_gastos_ppto`, `financiero.ejecucion_gastos_pct` (`MenorEsMejor`).
- `financiero.desviacion` = `SUM(Desviacion)` (dims periodo, sede, centro_costo, clase_puc, grupo_puc, cuenta). **Solo en tablas agrupadas por `clase_puc`, `grupo_puc` o `cuenta`, nunca como KPI total:** suma la desviación de ingresos con la de gastos, que tienen sentido contrario (ingresar 100 menos y gastar 100 menos daría −200). Los KPI son los dos porcentajes de ejecución y los dos valores presupuestados.
- Dashboard `financiero.presupuesto`: 4 KPI · línea real vs. presupuesto por periodo · tabla por grupo del PUC · tabla por centro de costo. Estado vacío: «No se ha cargado un presupuesto».
- Actualizar el comentario «Pendiente» de la clase.

### 4. Carga por portal (CSV, D58)
- Página `Components/Pages/Presupuesto.razor`: descargar plantilla CSV (`periodo;cuenta;centro_costo;sede;valor`), elegir año y versión, subir archivo.
- Application `Ingesta/PresupuestoPortal.cs`: `CargarPresupuestoCommand(Anio, Version, Stream)` → valida tamaño (≤ 2 MB) y filas (≤ 50.000), convierte a filas del contrato y **reutiliza el flujo existente**. No hay camino paralelo.
  - Cómo: el handler envía por `ISender` los mismos tres comandos de la API (`IniciarCargaCommand` con `VENTANA` enero–diciembre del año, `RecibirLoteCommand` en lotes de hasta 5.000 filas, `CerrarCargaCommand` con la suma de control). Sus handlers solo usan `ISesionUsuario.EmpresaId`, que en el portal sale de la sesión: funcionan igual. Así heredan validaciones, hash, `ExentaDeCierre` y la cola; el worker procesa la carga.
  - Archivo idéntico a uno ya procesado → `CARGA_DUPLICADA`: mostrar «sin cambios», no un error.
  - `RepositorioCargas.CrearAsync` escribe `Origen = 'api'` fijo. Para distinguir las cargas del portal, agregar `Origen` a `NuevaCarga` (por defecto `'api'`) y pasar `'portal'`: cambio opcional, no bloquea.
- Decimales: aceptar punto o coma según cultura `es-CO`, rechazar notación científica. Neutralizar celdas que empiecen por `= + - @` al mostrar errores.
- Resultado: enlace al estado de la carga y a sus rechazos (lista existente).
- Permiso `admin.usuarios` (D56); auditoría en `aud.CambioAdministrativo` (quién cargó qué versión y año, sin valores).
- Versión: v1 solo escribe `APROBADO` (la vista solo lee esa). Otras versiones quedan para cuando el tablero permita elegir.

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1090_presupuesto.sql` |
| Application | `Ingesta/Entidades.cs`, `Ingesta/Comandos.cs`, `Ingesta/PresupuestoPortal.cs` (nuevo) |
| Infrastructure | `Ingesta/Procesamiento.cs` |
| Módulo | `Modulos.Financiero/ModuloFinanciero.cs` |
| Web | `Components/Pages/Presupuesto.razor`, endpoint `/q/presupuesto` |
| Pruebas | `PresupuestoTests.cs` (nuevo), `Web/PresupuestoWebTests.cs`, `DashboardsTests.cs`, aislamiento |

Símbolos para `impact`: `CatalogoEntidades`, `EntidadIngesta`, `IniciarCargaHandler.Handle`, `ProcesadorCargas.AplicarAsync`, `ModuloFinancieroAnalitico.Registrar`, `IRepositorioCargas`.

## Verificado contra el código (2026-11-16)
- `EntidadIngesta` es `record (Nombre, Modos, ColumnasSumaControl, ModuloRequerido = null, EsCatalogo = false)`: `ExentaDeCierre = false` se agrega al final; el control de `IniciarCargaHandler` pasa de `{ EsCatalogo: false }` a `{ EsCatalogo: false, ExentaDeCierre: false }`.
- `rpt.vw_EjecucionPresupuestal` hace `FULL JOIN` entre `rpt.vw_EstadoResultados` y `fact.Presupuesto`; para dar `ClasePuc` y `Grupo` a las filas que solo tienen presupuesto hay que unir `dim.CuentaContable` por `COALESCE(r.CuentaId, p.CuentaId)`.
- `UQ_Presupuesto` incluye `CentroCostoId` y `SedeId` anulables: SQL Server trata los `NULL` como iguales en un índice único, así que el duplicado «sin centro» también se detecta en la tabla.
- `IniciarCargaCommandValidator` no limita fechas futuras: presupuestar meses futuros pasa. `dim.Fecha` llega hasta 2035.

## Pruebas
- Reemplazo por versión y ventana; otra versión intacta; cada rechazo.
- Caso conocido: presupuesto ingresos 1.000, real 900 → ejecución 90 %, desviación −100; gasto presupuestado 500, real 550 → 110 %.
- Cuenta con real y sin presupuesto, y al revés, aparecen (FULL JOIN).
- CSV: separador, decimales, archivo grande, fila mala no tumba las buenas (rechazo por fila).
- Aislamiento: presupuesto de A invisible para B; la carga por portal usa la empresa de la sesión.

## Riesgos y supuestos `[S]`
- El FULL JOIN por `CentroCostoId`/`SedeId` exige que el presupuesto esté al mismo detalle que la contabilidad; presupuestar sin centro y ejecutar con centro muestra desviaciones falsas por centro (el total sí cuadra). Advertirlo en la plantilla.
- ESE con plan CGN: las clases 4–7 no significan lo mismo. Mantener apagado para ESE hasta validar (`03` §22).
- Subida de archivos: primera superficie de carga por navegador; revisar con `/security-review` antes de mezclar.
