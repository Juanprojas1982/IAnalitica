# PL-05 · Cierre de periodo

Estado: diseñado, **bloqueado por DP-5**. No implementar sin esa decisión. Script `1085_cierre_periodo.sql`.

## Objetivo
Que una cifra reportada no cambie después: cerrar un mes en la plataforma congela sus hechos.

## Situación actual
- `etl.PeriodoCierre (EmpresaId, PeriodoKey, Estado, CerradoEn)` existe. `RepositorioCargas.PeriodoCerradoAsync` ya rechaza con `409 PERIODO_CERRADO` toda carga `VENTANA`/`INICIAL` de hechos cuya ventana toque un periodo cerrado. **Nada escribe en la tabla.**
- `ia.usp_CerrarPeriodo` es de la facturación de IA (D50): **no es parte de este plan**.

## Riesgo que obliga a decidir (DP-5)
El conector arma la ventana diaria «desde el primer periodo contable abierto en **SaludIPS** hasta hoy, tope 13 meses». Si IAnalitica cierra un mes que SaludIPS aún tiene abierto, **toda la carga nocturna de hechos recibe 409 y la empresa se queda sin datos nuevos**. Además, el rechazo es de la carga completa, no solo del mes cerrado.

Opciones:
| Opción | Qué hace | Costo |
|---|---|---|
| **A (recomendada)** | `GET /v1/estado` devuelve `periodoCerradoHasta`; el conector recorta `ventanaDesde` al primer día del mes siguiente. El 409 queda como defensa | Cambio en el conector (otro repo, net48) y nueva versión desplegada en cada IPS |
| B | La API recorta la ventana por su cuenta y procesa solo lo abierto | Rompe la semántica «VENTANA reemplaza todo el rango»; el conector cree que envió algo que se descartó |
| C | Cierre solo «blando»: no bloquea la ingesta; guarda una foto de los KPI del mes cerrado y avisa si después cambian | Sin riesgo operativo; no congela, pero detecta. Más tablas |

Mientras no haya conector actualizado en todos los clientes, solo C es segura. A y C no se excluyen.

## Cambios (opción A; ajustar si se elige otra)
### 1. Script `1085_cierre_periodo.sql`
`ALTER TABLE etl.PeriodoCierre ADD CerradoPor int NULL, ReabiertoEn datetime2(0) NULL, ReabiertoPor int NULL, MotivoReapertura nvarchar(300) NULL;`

### 2. Application `Ingesta/Periodos.cs` (nuevo)
- `ListarPeriodosQuery` (últimos 24 meses con estado, fecha y usuario).
- `CerrarPeriodoCommand(PeriodoKey)`: solo meses anteriores al mes en curso (hora Colombia); exige que todos los meses anteriores con datos estén cerrados (cierre consecutivo); no cerrar si hay una carga `CERRADA`/`PROCESANDO` de la empresa.
- `ReabrirPeriodoCommand(PeriodoKey, Motivo)`: motivo ≥ 10 caracteres; solo el último cerrado (reapertura en orden inverso).
- Ambos: `IComandoTransaccional`, permiso según DP-5 (`[S]` `admin.usuarios`), auditoría en `aud.CambioAdministrativo`, `EmpresaId` de la sesión.

### 3. API
`GET /v1/estado`: agregar `periodoCerradoHasta` (`int?`, yyyymm) a `EstadoPlataforma` en `IAnalitica.Ingesta.Contracts` (netstandard2.0; campo opcional = compatible con `/v1`).

### 4. Costos y cartera
- `CALCULAR_COSTOS` y el recálculo manual de `/costos` no deben recalcular periodos cerrados: filtrar en `EncolarCostosAsync` y en el comando de recálculo.
- La cartera es una foto por corte: no se ve afectada.

### 5. Web
`Components/Pages/Periodos.razor` (lista + cerrar + reabrir con motivo). Aviso visible: «cerrar impide que el sistema fuente corrija este mes».

### 6. Conector (repo `SaludIPS2`, fuera de este PR)
Leer `periodoCerradoHasta` y recortar la ventana. Documentar en `02` §16. Sin esto desplegado, **no habilitar el cierre para esa empresa** (bandera por empresa en `cfg.EmpresaParametro`: `CIERRE_PERIODO_HABILITADO`, por defecto apagada).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1085_cierre_periodo.sql` |
| Contracts | `src/IAnalitica.Ingesta.Contracts/Dtos.cs` |
| Application | `Ingesta/Periodos.cs` (nuevo), `Ingesta/Repositorio.cs` |
| Infrastructure | `Ingesta/RepositorioCargas.cs`, `Ingesta/Procesamiento.cs` |
| Web | `Components/Pages/Periodos.razor`, endpoints `/q/periodos` |
| Pruebas | `Application.Tests`, `Integration.Tests/Ingesta/PeriodosTests.cs`, `ApiIngestaTests.cs`, aislamiento |

Símbolos para `impact`: `EstadoPlataforma`, `IRepositorioCargas`, `RepositorioCargas.EstadoPlataformaAsync`, `ProcesadorCargas.EncolarCostosAsync`, `IniciarCargaHandler`.

## Pruebas
- Cerrar y reabrir con sus reglas (orden, mes en curso, motivo corto, carga en curso).
- Carga con ventana que toca un mes cerrado → 409; ventana posterior → 201.
- `GET /v1/estado` informa el cierre; sin cierres, campo nulo.
- Costos no recalculan un mes cerrado.
- Bandera apagada → la pantalla no permite cerrar.
- Aislamiento: cierres de A no afectan cargas de B.

## Riesgos
- Cambiar `Ingesta.Contracts` afecta al conector: solo agregar campos opcionales.
- Reabrir y recargar cambia cifras ya reportadas: la auditoría y el motivo son la única defensa; considerar la opción C como complemento.
