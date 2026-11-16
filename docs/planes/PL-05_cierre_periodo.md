# PL-05 · Cierre de periodo

Estado: diseñado, no implementado. Script `1085_cierre_periodo.sql`.
Preparación (2026-11-16): **fase 1 (cierre blando) lista para implementar.** DP-5 aprobada (D57): blando ahora, duro después. La fase 2 (cierre duro) se habilita empresa por empresa cuando su conector esté actualizado.
Depende de PL-04: el trabajo `ALERTAS`, la sesión de sistema y las notificaciones compartidas.

## Objetivo
Que una cifra reportada no cambie sin que alguien se entere (fase 1) y, donde el conector lo permita, que no cambie (fase 2).

## Situación actual
- `etl.PeriodoCierre (EmpresaId, PeriodoKey, Estado, CerradoEn)` existe. `RepositorioCargas.PeriodoCerradoAsync` ya rechaza con `409 PERIODO_CERRADO` toda carga `VENTANA`/`INICIAL` de hechos cuya ventana toque un periodo con `Estado = 'CERRADO'`. **Nada escribe en la tabla.** El control vive en `IniciarCargaHandler` y aplica a toda entidad que no sea catálogo; las entidades con `ExentaDeCierre` (`presupuesto`, `agenda-capacidad`; ver índice, «Mecanismos compartidos») no pasan por él.
- `ia.usp_CerrarPeriodo` es de la facturación de IA (D50): **no es parte de este plan**.

## Por qué dos fases (D57)
El conector arma la ventana diaria «desde el primer periodo contable abierto en **SaludIPS** hasta hoy, tope 13 meses». Si IAnalitica cierra en duro un mes que SaludIPS aún tiene abierto, **toda la carga nocturna de hechos recibe 409 y la empresa se queda sin datos nuevos** (el rechazo es de la carga completa, no solo del mes cerrado). Por eso:

| Fase | Tipo | Qué hace | Requisito |
|---|---|---|---|
| 1 | `BLANDO` | No bloquea la ingesta. Guarda una foto de los KPI del mes y avisa si después cambian | Ninguno |
| 2 | `DURO` | Además bloquea la ingesta del mes (409). `GET /v1/estado` informa `periodoCerradoHasta` y el conector recorta la ventana | Conector actualizado en esa empresa y bandera encendida |

Descartada: que la API recorte la ventana por su cuenta (rompe la semántica «VENTANA reemplaza todo el rango»).

## Fase 1 · Cierre blando
### 1. Script `1085_cierre_periodo.sql`
```sql
ALTER TABLE etl.PeriodoCierre ADD
    Tipo varchar(6) NOT NULL CONSTRAINT DF_PC_Tipo DEFAULT 'DURO' CONSTRAINT CK_PC_Tipo CHECK (Tipo IN ('BLANDO','DURO')),
    CerradoPor int NULL, ReabiertoEn datetime2(0) NULL, ReabiertoPor int NULL, MotivoReapertura nvarchar(300) NULL;

CREATE TABLE etl.PeriodoCierreKpi (
    EmpresaId     int NOT NULL CONSTRAINT FK_PCK_Empresa REFERENCES seg.Empresa,
    PeriodoKey    int NOT NULL,
    MetricaCodigo varchar(80) NOT NULL,
    ValorCierre   decimal(19,4) NULL,          -- valor al cerrar
    ValorActual   decimal(19,4) NULL,          -- último valor recalculado (NULL = sin revisar aún)
    RevisadoEn    datetime2(0) NULL,
    CambioEn      datetime2(0) NULL,           -- primera vez que se detectó diferencia frente a ValorCierre
    CONSTRAINT PK_PeriodoCierreKpi PRIMARY KEY (EmpresaId, PeriodoKey, MetricaCodigo),
    CONSTRAINT FK_PCK_Periodo FOREIGN KEY (EmpresaId, PeriodoKey) REFERENCES etl.PeriodoCierre (EmpresaId, PeriodoKey)
);
```
- El `DEFAULT 'DURO'` conserva el significado de las filas que pudieran existir (hoy la tabla está vacía en todos los ambientes: nadie la escribe).
- **`PeriodoCerradoAsync` pasa a filtrar `Estado = 'CERRADO' AND Tipo = 'DURO'`**: un cierre blando nunca produce 409. Es el cambio crítico de la fase.
- RLS: ambas tablas tienen `EmpresaId` (política automática). Ajustar el largo de `MetricaCodigo` al de los códigos del catálogo si es mayor.

### 2. Qué se guarda en la foto
Métricas que aparecen en widgets `TipoWidget.Kpi` de los dashboards de fábrica de los **módulos activos** de la empresa y que tienen dimensión `periodo`; sin dimensiones (total empresa), con `PeriodoDesde = PeriodoHasta = periodo` (las `Acumulado` dan el saldo a ese mes). Quedan fuera las métricas por `corte` (cartera): una foto por corte ya es inmutable.
Se evalúan con `IMotorMetricas` directamente (no por los handlers de dashboards): la foto no depende de los permisos `dash.*` de quien cierra.

### 3. Application `Ingesta/Periodos.cs` (nuevo)
- `ListarPeriodosQuery`: últimos 24 meses con estado, tipo, fecha, usuario y número de KPI que cambiaron tras el cierre.
- `CerrarPeriodoCommand(PeriodoKey)`: solo meses anteriores al mes en curso (hora Colombia); cierre consecutivo (todos los meses anteriores con datos ya cerrados); no cerrar si hay una carga `CERRADA`/`PROCESANDO` de la empresa. Escribe `Tipo = 'BLANDO'` y la foto en la misma transacción. En fase 1 el comando **no acepta** `DURO`.
- `ReabrirPeriodoCommand(PeriodoKey, Motivo)`: motivo ≥ 10 caracteres; solo el último cerrado. Borra la foto.
- `AceptarCambiosPeriodoCommand(PeriodoKey, Motivo)`: reconoce los cambios detectados y retoma la foto (`ValorCierre = ValorActual`, `CambioEn = NULL`); motivo ≥ 10 caracteres.
- Los tres: `IComandoTransaccional`, permiso `admin.usuarios` (D56/D57), auditoría en `aud.CambioAdministrativo` (valores anteriores y nuevos), `EmpresaId` de la sesión, nunca del cuerpo.

### 4. Detección de cambios (en el trabajo `ALERTAS` de PL-04)
Después de evaluar las metas, `ManejadorAlertas` llama a `IRevisorPeriodosCerrados`:
- Para cada periodo `CERRADO` de la empresa en los últimos 13 meses (la ventana máxima del conector), reevalúa las métricas de su foto con la sesión de sistema.
- Hay cambio si `|actual − cierre| ≥ 1` en métricas en pesos, o `≥ 0,01` en las demás (porcentajes, días, veces, unidades). Nulo frente a valor cuenta como cambio.
- Actualiza `ValorActual` y `RevisadoEn`; pone `CambioEn` la primera vez.
- Notifica `PERIODO_CAMBIO` (`UsuarioId NULL`, enlace a `/periodos`) **solo cuando aparece un cambio nuevo** (un `ValorActual` distinto del anterior): «3 indicadores de 2026-03 cambiaron después del cierre». Sin cifras de personas.
- Una métrica que ya no existe en el catálogo o cuyo módulo se desactivó se omite sin fallar.

### 5. Web
`Components/Pages/Periodos.razor` (lista + cerrar + reabrir + aceptar cambios, con motivo). Por periodo cerrado: tabla métrica · valor al cierre · valor actual · diferencia. Texto fijo en fase 1: «El cierre no impide que el sistema fuente corrija el mes; le avisa si las cifras cambian».

### 6. Costos y cartera
En cierre blando **no** se filtra el recálculo de costos (no congela). La cartera es una foto por corte: no se ve afectada.

## Fase 2 · Cierre duro (por empresa)
Sin script nuevo: usa `Tipo = 'DURO'`.
- Bandera por empresa en `cfg.EmpresaParametro`: `CIERRE_DURO_HABILITADO` (por defecto apagada; la enciende el superadmin cuando el conector de esa empresa esté actualizado). Con la bandera encendida, `CerrarPeriodoCommand` acepta el tipo `DURO` y la pantalla ofrece «convertir a cierre definitivo» sobre un periodo blando.
- `GET /v1/estado`: agregar `periodoCerradoHasta` (`int?`, yyyymm; solo cierres `DURO`) a `EstadoPlataforma` en `IAnalitica.Ingesta.Contracts` (netstandard2.0; campo opcional = compatible con `/v1`).
- `CALCULAR_COSTOS` y el recálculo manual de `/costos` no recalculan periodos con cierre `DURO`: filtrar en `EncolarCostosAsync` y en el comando de recálculo.
- Conector (repo `SaludIPS2`, fuera de este PR): leer `periodoCerradoHasta` y recortar `ventanaDesde` al primer día del mes siguiente. Documentar en `02` §16. El 409 queda como defensa.
- La foto y la detección de la fase 1 siguen activas en cierres duros (deberían no cambiar: si cambian, es una alerta de integridad).

## Archivos por capa
| Capa | Archivo | Fase |
|---|---|---|
| BD | `database/scripts/1085_cierre_periodo.sql` | 1 |
| Application | `Ingesta/Periodos.cs` (nuevo), `Ingesta/Repositorio.cs` | 1 |
| Infrastructure | `Ingesta/RepositorioCargas.cs` (`PeriodoCerradoAsync`), `Ingesta/RepositorioPeriodos.cs` (nuevo), `Semantica/RevisorPeriodosCerrados.cs` (nuevo), `Semantica/ManejadorAlertas.cs`, `DependencyInjection.cs` | 1 |
| Web | `Components/Pages/Periodos.razor`, endpoints `/q/periodos`, menú | 1 |
| Contracts | `src/IAnalitica.Ingesta.Contracts/Dtos.cs` | 2 |
| Infrastructure | `Ingesta/Procesamiento.cs` (`EncolarCostosAsync`), `RepositorioCargas.EstadoPlataformaAsync` | 2 |
| Pruebas | `Application.Tests`, `Integration.Tests/Ingesta/PeriodosTests.cs`, `ApiIngestaTests.cs`, `AlertasTests.cs`, aislamiento | 1 y 2 |

Símbolos para `impact`: `IRepositorioCargas`, `RepositorioCargas.PeriodoCerradoAsync`, `IniciarCargaHandler`, `ManejadorAlertas`, `IMotorMetricas`; en fase 2 `EstadoPlataforma`, `RepositorioCargas.EstadoPlataformaAsync`, `ProcesadorCargas.EncolarCostosAsync`.

## Pruebas
Fase 1:
- Cerrar y reabrir con sus reglas (orden, mes en curso, motivo corto, carga en curso); sin permiso → 403; auditoría escrita.
- **Cierre blando no bloquea:** carga `VENTANA` que toca el mes cerrado → 201 (regresión de `ApiIngestaTests`: una fila `DURO` sigue dando 409).
- Foto: ingresos 1.000 al cerrar; recarga que los deja en 1.200 → tras `ALERTAS`, `ValorActual = 1.200`, `CambioEn` con fecha y **una** notificación; segunda corrida sin cambios → ninguna; nueva recarga a 1.300 → otra.
- Diferencia de 0,50 pesos no es cambio; de 0,02 puntos porcentuales sí.
- Aceptar cambios retoma la foto y limpia el aviso.
- Métricas por `corte` no entran en la foto; un módulo desactivado después no rompe la revisión.
- Aislamiento: cierres, fotos y avisos de A invisibles para B y sin efecto en sus cargas.

Fase 2:
- Bandera apagada → no se puede cerrar en duro. Encendida: ventana que toca el mes → 409; posterior → 201.
- `GET /v1/estado` informa solo cierres duros; sin ellos, campo nulo.
- Costos no recalculan un mes con cierre duro; sí uno con cierre blando.

## Riesgos
- El cierre blando no congela: el nombre en pantalla debe decirlo («cierre con seguimiento», no «cierre definitivo»).
- Costo del trabajo nocturno: hasta 13 periodos × los KPI de la empresa, una consulta cada uno. Medir; si pesa, revisar solo los periodos tocados por la ventana de las cargas de esa noche.
- Un cambio de definición de una métrica (nueva versión del producto) dispara avisos en todos los cierres: al desplegar un cambio así, anotarlo en la nota de versión y usar «aceptar cambios».
- Cambiar `Ingesta.Contracts` afecta al conector: solo agregar campos opcionales (fase 2).
- Reabrir y recargar cambia cifras ya reportadas: la auditoría y el motivo son la defensa.
