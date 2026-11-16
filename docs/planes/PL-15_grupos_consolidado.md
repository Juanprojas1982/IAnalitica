# PL-15 · Grupos de instituciones (vista consolidada)

Estado: diseñado, **bloqueado por decisión DP-9**. Script `1170_grupos.sql`.
Contradice D11 («usuario de una sola empresa; superadmin ve todas») y toca la RLS: es el plan de mayor riesgo de seguridad. No implementar sin aprobación explícita del modelo.

## Objetivo
Que la dirección de un grupo con varias empresas (varios NIT) vea indicadores consolidados y compare empresas, sin que los usuarios de una empresa vean las otras.

## Lo que NO se hace
- No se relaja el aislamiento: un usuario normal sigue viendo solo su empresa.
- No hay consulta libre entre empresas: solo métricas agregadas del catálogo.
- **Sin eliminaciones entre compañías** en v1 (la spec las pone en fase 2): el consolidado es una **suma**, y así se rotula («agregado del grupo, sin eliminación de operaciones recíprocas»). Un consolidado contable real exige identificar terceros vinculados: fuera de alcance.

## Decisión requerida (DP-9): modelo de acceso
| Opción | Cómo | Riesgo |
|---|---|---|
| **A (recomendada)** | Tabla `seg.Grupo` + `seg.GrupoEmpresa` + `seg.UsuarioGrupo`. El usuario de grupo sigue perteneciendo a **una** empresa (D11 intacta) y recibe además el permiso `grupo.ver`. Las consultas de grupo **no usan su conexión**: las ejecuta un servicio que itera empresa por empresa con el contexto de cada una y solo devuelve agregados | Bajo: la RLS no cambia; ninguna sesión ve dos empresas a la vez |
| B | Ampliar el predicado de RLS: `EmpresaId IN (empresas del grupo del usuario)` | Alto: cambia `seg.PolTenant` para todas las tablas; un error expone datos entre empresas |
| C | Empresa «holding» que recibe una copia agregada nocturna | Medio: duplica datos; desfase de un día; más almacenamiento |

## Diseño (opción A)
### Script `1170_grupos.sql`
```sql
CREATE TABLE seg.Grupo (GrupoId int IDENTITY PRIMARY KEY, Nombre nvarchar(150) NOT NULL, Activo bit NOT NULL DEFAULT 1);
CREATE TABLE seg.GrupoEmpresa (GrupoId int NOT NULL REFERENCES seg.Grupo, EmpresaId int NOT NULL REFERENCES seg.Empresa, PRIMARY KEY (GrupoId, EmpresaId));
CREATE TABLE seg.UsuarioGrupo (EmpresaId int NOT NULL REFERENCES seg.Empresa, UsuarioId int NOT NULL REFERENCES seg.Usuario(Id), GrupoId int NOT NULL REFERENCES seg.Grupo, PRIMARY KEY (UsuarioId, GrupoId));   -- EmpresaId = empresa del usuario: la exige la RLS automática
```
- **RLS (parte de DP-9; se decide en diseño, no en implementación).** `9000_rls.sql` solo cubre tablas con `EmpresaId`:
  - `seg.Grupo` y `seg.UsuarioGrupo` tal como están arriba **no tienen `EmpresaId` y quedan sin política**. `seg.UsuarioGrupo` debe llevar `EmpresaId int NOT NULL` (la empresa del usuario, D11) para entrar en la política automática. `seg.Grupo` es global (solo nombre): lectura por la aplicación con verificación explícita de pertenencia; escritura solo superadmin (`admin.empresas`).
  - `seg.GrupoEmpresa` sí entra en la política: cada empresa ve su propia fila (correcto: debe saber que pertenece a un grupo), pero el servicio de grupo, con la sesión del usuario (empresa A), **no puede leer las filas de B** y no sabría qué empresas iterar.
  - Candidatas para esa lectura: **(a, recomendada)** un repositorio dedicado con una sola consulta fija sobre `seg.GrupoEmpresa ⨝ seg.UsuarioGrupo` por la conexión técnica `rol_etl` (ese rol ya está exento en `fn_FiltroEmpresa`), que recibe `UsuarioId` y `GrupoId` y devuelve solo `EmpresaId`; nunca lee `rpt.*` ni `fact.*`. **(b)** política propia para `seg.GrupoEmpresa` en `9000_rls.sql` que permita leer las filas de los grupos del usuario (necesita `UsuarioId` en `SESSION_CONTEXT` y una subconsulta en el predicado: más superficie en la RLS, que es justo lo que la opción A del modelo evita).
  - Sin esta elección aprobada el plan no pasa a implementación.
- Permiso nuevo `grupo.ver` (script de semillas nuevo; amplía P10).

### Application `Grupos/`
- `ObtenerMetricaGrupoQuery(GrupoId, Metrica, Filtros)`: valida que el usuario esté en `seg.UsuarioGrupo`; obtiene las empresas del grupo; para cada una crea un **alcance de sesión de sistema** con `EmpresaId = n` (el de PL-04 §2: `SesionTrabajo.EstablecerSistema` + `ContextoBd.DeApi`; aquí se usa desde la web y no desde el worker, así que el plan debe exponerlo como servicio interno de Infrastructure, sin endpoint que reciba `EmpresaId`) y ejecuta `IMotorMetricas` sin dimensiones o con `periodo`; devuelve `{empresa, valor}` + total.
- Solo métricas aditivas (expresión `SUM(col)`) se totalizan; las razones se recalculan desde sus componentes o se muestran solo por empresa, **nunca se suman ni se promedian**. Lista blanca de métricas de grupo declarada por cada módulo (`IRegistroModulo.MetricaDeGrupo`), no todas.
- Cada consulta queda en `aud.Acceso` de **cada** empresa consultada («consulta de grupo por usuario X»): la empresa debe poder ver quién la mira.
- Caché por grupo + versión de carga de cada empresa.

### Web
`Components/Pages/Grupo.razor`: selector de grupo, KPI del total, tabla y barras por empresa. Sin drill a los tableros de otra empresa (el usuario no tiene sesión en ella).

### Alta
Solo el superadmin crea grupos y asigna empresas y usuarios; auditado en `aud.CambioAdministrativo`. Requiere el consentimiento contractual de cada empresa del grupo (texto para el abogado, P5).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1170_grupos.sql` |
| Domain | `Modulos/IModuloAnalitico.cs` (`MetricaDeGrupo`) |
| Application | `Grupos/*` (nuevo) |
| Infrastructure | `Grupos/*` (nuevo), `DependencyInjection.cs` |
| Web | `Components/Pages/Grupo.razor`, `Admin/Grupos.razor` |
| Pruebas | `GruposTests.cs`, `AislamientoEntreEmpresasTests.cs` (ampliar), web |

Símbolos para `impact`: `IRegistroModulo`, `CatalogoAnalitico`, `IMotorMetricas`, el interceptor de `SESSION_CONTEXT`, `ISesionUsuario`.

## Pruebas (las de seguridad son la definición de terminado)
- Usuario de grupo G (empresas A, B) no obtiene nada de C; usuario de A sin grupo no accede al endpoint.
- `seg.UsuarioGrupo` y `seg.GrupoEmpresa` aparecen en `seg.PolTenant`; un usuario de A no lee por SQL directo (`rol_app`) la membresía de B.
- La lectura técnica de membresía solo devuelve empresas de grupos a los que pertenece el usuario pedido.
- Usuario de grupo **no** puede leer filas de B por ningún tablero normal ni por el asistente.
- Quitar una empresa del grupo corta el acceso en la siguiente consulta (sin esperar caché).
- Total = suma de las empresas para una métrica aditiva; una razón no se totaliza.
- La consulta queda auditada en cada empresa.

## Riesgos
- La sesión de sistema por empresa es un mecanismo potente: debe ser interno, no inyectable desde un endpoint, y con pruebas propias. Si PL-04 ya lo creó, reutilizarlo.
- Empresas del grupo con planes de cuentas o reglas de costos distintos: la suma mezcla criterios. Mostrarlo.
- Comercial: un «grupo» puede ser un mismo dueño con varios NIT o instituciones independientes; el contrato debe distinguirlo.
