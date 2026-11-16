# PL-08 · Deterioro estimado de cartera

Estado: diseñado, no implementado. Módulo `CARTERA`. Script `1100_deterioro_cartera.sql`.
Preparación (2026-11-16): **listo para implementar.** DP-7 aprobada (D59) y permiso `admin.usuarios` (D56).
Depende de PL-04 solo por la versión de configuración en la clave de caché; si PL-04 no está mezclado, se crea aquí con la definición de PL-04 §1.
Origen: propuesta del análisis, **no está en la spec**. Aprobado por el dueño como alcance nuevo (D59).

## Objetivo
Mostrar cuánto de la cartera por cobrar es razonable esperar no recaudar, según una política parametrizada por la empresa, y compararlo con lo que tiene registrado en contabilidad.

## Límite explícito
Es una **estimación gerencial**, no el cálculo contable de deterioro (NIIF 9 / política de cada entidad / marco de la Contaduría para ESE). El tablero debe decirlo. No genera asientos.

## Cambios
### 1. Script `1100_deterioro_cartera.sql`
```sql
CREATE TABLE cfg.DeterioroCartera (
    EmpresaId   int NOT NULL CONSTRAINT FK_DC_Empresa REFERENCES seg.Empresa,
    RangoEdad   varchar(10) NOT NULL,          -- mismos rangos de fact.CarteraDocumento
    PagadorId   int NULL CONSTRAINT FK_DC_Pagador REFERENCES dim.Pagador,   -- NULL = regla general; con valor = excepción (p. ej. EPS en liquidación)
    Porcentaje  decimal(5,2) NOT NULL CONSTRAINT CK_DC_Pct CHECK (Porcentaje BETWEEN 0 AND 100),
    ActualizadoEn datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    ActualizadoPor int NULL
);
CREATE UNIQUE INDEX UX_DeterioroCartera ON cfg.DeterioroCartera (EmpresaId, RangoEdad, PagadorId);
```
(RLS automática por `9000_rls.sql`.) Sin semillas: **sin configuración no hay cifra** (no inventar porcentajes por defecto).

**Vista `rpt.vw_DeterioroCartera`** sobre `fact.CarteraDocumento` `TipoCartera = 'CXC'`:
`EmpresaId, CorteFechaKey, PagadorId, SedeId, RangoEdad, Saldo, Porcentaje` (regla del pagador si existe; si no, la general; si no hay ninguna, NULL)`, Deterioro` (= `Saldo × Porcentaje / 100`)`, SaldoSinRegla` (saldo de filas sin porcentaje).

**Vista `rpt.vw_DeterioroContable`** (opcional; fuera de la primera entrega): saldo acumulado de las cuentas de deterioro de cartera. La identificación de esas cuentas es `[S]` (PUC comercial: subcuentas de provisión del grupo 13; ESE distinto) → se parametriza con una lista de prefijos en `cfg.EmpresaParametro` (`DETERIORO_CUENTAS`), no en código.

### 2. Métricas y dashboard (`ModuloCartera`)
`cartera.deterioro_estimado` = `SUM(Deterioro)`, `cartera.cartera_neta` = `SUM(Saldo) - SUM(Deterioro)` → **no cabe en el catálogo cerrado de expresiones**: exponer `SaldoNeto` como columna de la vista y usar `SUM(SaldoNeto)`. `cartera.pct_deterioro` = `100.0*SUM(Deterioro)/NULLIF(SUM(Saldo),0)`, `cartera.saldo_sin_regla` = `SUM(SaldoSinRegla)`.
Dashboard `cartera.deterioro`: KPI estimado, % sobre cartera, cartera neta, saldo sin regla · barras por rango de edad · tabla por pagador. Aviso fijo: «Estimación según la política configurada; no reemplaza el cálculo contable».

### 3. Configuración
- Application `Semantica/DeterioroCartera.cs` (o carpeta `Cartera/`): `ListarReglasDeterioroQuery`, `GuardarReglasDeterioroCommand` (reemplaza el conjunto de la empresa; valida rangos del catálogo y un pagador de la empresa), auditoría.
- Web: `Components/Pages/DeterioroCartera.razor` (tabla rango × porcentaje + excepciones por pagador). Permiso `admin.usuarios` (D56).
- Al guardar: incrementar la versión de configuración de la empresa (`IVersionConfiguracion`, PL-04 §1); la caché no tiene invalidación directa.

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1100_deterioro_cartera.sql` |
| Application | `Cartera/DeterioroCartera.cs` (nuevo) |
| Infrastructure | `Cartera/RepositorioDeterioro.cs` (nuevo), `DependencyInjection.cs` |
| Módulo | `Modulos.Ingreso/ModuloIngreso.cs` (`ModuloCartera`) |
| Web | `Components/Pages/DeterioroCartera.razor`, endpoint `/q/deterioro` |
| Pruebas | `DeterioroCarteraTests.cs`, web, aislamiento |

Símbolos para `impact`: `ModuloCartera.Registrar`, `Dimensiones.Todas` (no cambia, verificar `rango_edad`).

## Pruebas
- Cartera 100 en `>360` al 100 % y 200 en `0-30` al 0 % → deterioro 100, neta 200, 33,3 %.
- Excepción por pagador prevalece sobre la general.
- Sin reglas: deterioro NULL, `saldo_sin_regla` = cartera total, el tablero muestra «configure la política».
- Aislamiento: reglas y cifras de A invisibles para B; no se puede guardar una excepción con un pagador de otra empresa.

## Riesgos
- Responsabilidad: que el cliente lo tome como cifra contable. Texto en interfaz y en contrato, como en el validador de precios.
- EPS en liquidación: el estado del pagador no existe en `dim.Pagador`; la excepción por pagador lo cubre a mano.
