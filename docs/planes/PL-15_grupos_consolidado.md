# PL-15 · Grupos de instituciones (vista consolidada)

Estado: diseñado, no implementado. Script `1170_grupos.sql`.
Preparación (2026-11-16): **no listo: faltan dos confirmaciones del dueño (DP-14 y DP-15, abajo).** DP-9 aprobada (D60): opción A y lectura técnica acotada de la membresía. La revisión contra el código encontró que D60, tal como está escrita, no se puede implementar sin ampliar lo que decidió: (1) el portal no tiene conexión `rol_etl`; (2) sin tocar `9000_rls.sql` un usuario de una empresa podría inscribirse a sí mismo en un grupo. El resto del diseño está completo. Dar de alta un grupo real exige además el consentimiento contractual (P5).
Matiza D11 («usuario de una sola empresa; superadmin ve todas») sin cambiarla: el usuario sigue en una empresa y el predicado de aislamiento no se relaja (lo único que se pide tocar en `9000_rls.sql` es **endurecer** la escritura de dos tablas nuevas, DP-15). Sigue siendo el plan de mayor riesgo de seguridad: las pruebas de seguridad son su definición de terminado.

## Objetivo
Que la dirección de un grupo con varias empresas (varios NIT) vea indicadores consolidados y compare empresas, sin que los usuarios de una empresa vean las otras.

## Lo que NO se hace
- No se relaja el aislamiento: un usuario normal sigue viendo solo su empresa.
- No hay consulta libre entre empresas: solo métricas agregadas del catálogo.
- **Sin eliminaciones entre compañías** en v1 (la spec las pone en fase 2): el consolidado es una **suma**, y así se rotula («agregado del grupo, sin eliminación de operaciones recíprocas»). Un consolidado contable real exige identificar terceros vinculados: fuera de alcance.

## Modelo de acceso (DP-9 aprobada: opción A)
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
- **RLS (decidido en D60).** `9000_rls.sql` solo cubre tablas con `EmpresaId`:
  - `seg.UsuarioGrupo` lleva `EmpresaId` (la empresa del usuario, D11) y `seg.GrupoEmpresa` también: ambas entran en la política automática. `seg.Grupo` es global (solo nombre, sin `EmpresaId`, sin política): lectura por la aplicación con verificación explícita de pertenencia; escritura solo superadmin (`admin.empresas`).
  - Con la política por defecto cada empresa ve su propia fila de `seg.GrupoEmpresa` (correcto: debe saber que pertenece a un grupo), pero el servicio de grupo, con la sesión del usuario (empresa A), **no puede leer las filas de B** y no sabría qué empresas iterar.
  - Lectura de la membresía: **(a, aprobada en D60)** una sola consulta fija sobre `seg.GrupoEmpresa ⨝ seg.UsuarioGrupo` con privilegio de `rol_etl` (exento en `fn_FiltroEmpresa`), que recibe `UsuarioId` y `GrupoId` y devuelve solo `EmpresaId`; nunca lee `rpt.*` ni `fact.*`. **(b, descartada)** política propia para `seg.GrupoEmpresa` que permita leer las filas de los grupos del usuario (subconsulta en el predicado: más superficie en la RLS).
  - Es la **única** excepción a «no usar `rol_etl` fuera de la ingesta». Una sola consulta parametrizada y una prueba que falla si se le agrega otra tabla.

### Dos huecos que la revisión encontró (bloquean; decide el dueño)
**DP-14 · Por dónde se ejecuta la consulta de membresía.** D60 dice «por la conexión técnica (`rol_etl`)», pero **el host Web no tiene esa conexión**: solo llama a `AddInfrastructure(cadena de rol_app)`; `IFabricaConexionesEtl` se registra en `AddProcesamiento`, que solo usa el Worker, y su contrato dice «nunca las usan el portal ni la API» (también `0900_roles_permisos.sql`).
| Opción | Cómo | Efecto |
|---|---|---|
| **1 (recomendada)** | Procedimiento `seg.usp_EmpresasDeGrupo @UsuarioId, @GrupoId` con `EXECUTE AS` un usuario de BD sin login, miembro de `rol_etl`, creado en `1170`; `GRANT EXECUTE` solo de ese procedimiento a `rol_app`. El portal lo llama con su conexión normal | El privilegio de `rol_etl` queda encerrado en una consulta fija dentro de la BD; el portal no recibe ninguna credencial nueva. Hay que probar que `IS_MEMBER('rol_etl')` se evalúa como verdadero bajo `EXECUTE AS` (lo usa `fn_FiltroEmpresa`); si no, esta opción no sirve y se vuelve a diseño |
| 2 | Dar al portal la cadena de conexión `rol_etl` | Literal a D60, pero el proceso expuesto a internet tendría una credencial que salta toda la RLS (hoy solo la tiene el Worker). Contradice `01` §3 |
| 3 | Que el Worker resuelva la consulta de grupo como trabajo en cola | Sin credencial nueva en el portal, pero la pantalla deja de ser inmediata (cola + sondeo) |

**DP-15 · Quién puede escribir la membresía.** La política por defecto solo bloquea escribir filas de **otra** empresa (`BLOCK … fn_FiltroEmpresa`). `seg.UsuarioGrupo` lleva el `EmpresaId` del usuario, así que una sesión de la empresa A puede insertar `(EmpresaId = A, UsuarioId = propio, GrupoId = cualquiera)` y **entrar a un grupo ajeno**; la aplicación no lo ofrece, pero la RLS —que es la segunda defensa— no lo impide. Igual con `seg.GrupoEmpresa` (meter o sacar la propia empresa de un grupo).
- Corrección propuesta: en `siempre/9000_rls.sql`, agregar `seg.UsuarioGrupo` y `seg.GrupoEmpresa` al caso que ya existe para `fac.%` (filtro `fn_FiltroEmpresa` para leer lo propio; `BLOCK` con `fn_SoloSuperadmin` en `AFTER INSERT`, `AFTER UPDATE`, `BEFORE UPDATE` y `BEFORE DELETE`): leen los suyos, **escribe solo el superadmin**. Son dos nombres en una lista; no cambia el predicado de ninguna otra tabla.
- Esto **sí toca `9000_rls.sql`**, que D60 daba por intacto («la RLS no cambia»). Por eso se pide confirmación. Sin esta corrección el plan no debe implementarse.
- Permiso nuevo `grupo.ver`, sembrado en el mismo `1170_grupos.sql` sin asignarlo a ningún rol de fábrica (lo otorga el superadmin por usuario); amplía P10.

### Application `Grupos/`
- `ObtenerMetricaGrupoQuery(GrupoId, Metrica, Filtros)`: `[RequierePermiso("grupo.ver")]`; valida que el usuario esté en `seg.UsuarioGrupo`; obtiene las empresas del grupo (DP-14); para cada una crea un **alcance de sistema** con `IFabricaAlcanceSistema.Crear(n)` (PL-04 §3: sesión explícita + `ContextoBd.DeApi(n)`; está diseñado para funcionar en el host Web, donde `ISesionUsuario` del contenedor es siempre la sesión HTTP del usuario) y ejecuta `AlcanceSistema.Motor` sin dimensiones o con `periodo`; devuelve `{empresa, valor}` + total. El handler vive en Infrastructure o recibe un servicio de Infrastructure (`IConsultaGrupo`): Application no conoce el alcance de sistema y ningún comando recibe un `EmpresaId`.
  - Antes de evaluar en cada empresa: `AlcanceSistema.Modulos.EstaActivoAsync` del módulo de la métrica; si no está activo, esa empresa sale «sin módulo», no en 0.
  - El `GrupoId` llega del cliente: es un identificador que se valida contra la membresía del usuario de la sesión, no un `EmpresaId`.
- Solo métricas aditivas (expresión `SUM(col)`) se totalizan; las razones se recalculan desde sus componentes o se muestran solo por empresa, **nunca se suman ni se promedian**. Lista blanca de métricas de grupo declarada por cada módulo (`IRegistroModulo.MetricaDeGrupo`), no todas.
- Cada consulta queda en `aud.Acceso` de **cada** empresa consultada («consulta de grupo por usuario X»): la empresa debe poder ver quién la mira. Se inserta con el alcance de sistema de esa empresa (`rol_app` tiene `INSERT` sobre `aud`; el registro lleva el `UsuarioId` real como dato, aunque el contexto de BD no tenga usuario).
- Caché por grupo + versión de carga de cada empresa.

### Web
`Components/Pages/Grupo.razor`: selector de grupo, KPI del total, tabla y barras por empresa. Sin drill a los tableros de otra empresa (el usuario no tiene sesión en ella).

### Alta
Solo el superadmin crea grupos y asigna empresas y usuarios; auditado en `aud.CambioAdministrativo`. Requiere el consentimiento contractual de cada empresa del grupo (texto para el abogado, P5).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1170_grupos.sql`; `database/scripts/siempre/9000_rls.sql` (DP-15) |
| Domain | `Modulos/IModuloAnalitico.cs` (`MetricaDeGrupo`) |
| Application | `Grupos/*` (nuevo) |
| Infrastructure | `Grupos/*` (nuevo), `DependencyInjection.cs` |
| Web | `Components/Pages/Grupo.razor`, `Admin/Grupos.razor` |
| Pruebas | `GruposTests.cs`, `AislamientoEntreEmpresasTests.cs` (ampliar), web |

Símbolos para `impact`: `IRegistroModulo`, `CatalogoAnalitico`, `IMotorMetricas`, el interceptor de `SESSION_CONTEXT`, `ISesionUsuario`.

## Pruebas (las de seguridad son la definición de terminado)
- Usuario de grupo G (empresas A, B) no obtiene nada de C; usuario de A sin grupo no accede al endpoint.
- `seg.UsuarioGrupo` y `seg.GrupoEmpresa` aparecen en `seg.PolTenant`; un usuario de A no lee por SQL directo (`rol_app`) la membresía de B.
- **Escritura (DP-15):** con el contexto de la empresa A y `rol_app`, `INSERT`, `UPDATE` y `DELETE` sobre `seg.UsuarioGrupo` y `seg.GrupoEmpresa` fallan, también sobre filas con `EmpresaId = A`; con el superadmin funcionan.
- **Membresía (DP-14):** `rol_app` no puede leer `seg.GrupoEmpresa` de otra empresa por ninguna vía distinta de la consulta fija; esa consulta con un `UsuarioId` que no pertenece al grupo devuelve cero filas.
- El alcance de sistema creado dentro de una solicitud web de un usuario de A para la empresa B lee B y, al cerrarse, la conexión del usuario sigue viendo solo A.
- La lectura técnica de membresía solo devuelve empresas de grupos a los que pertenece el usuario pedido.
- Usuario de grupo **no** puede leer filas de B por ningún tablero normal ni por el asistente.
- Quitar una empresa del grupo corta el acceso en la siguiente consulta (sin esperar caché).
- Total = suma de las empresas para una métrica aditiva; una razón no se totaliza.
- La consulta queda auditada en cada empresa.

## Riesgos
- El alcance de sistema por empresa (PL-04 §3) es un mecanismo potente: debe ser interno, no inyectable desde un endpoint, y con pruebas propias. Si PL-04 no está mezclado, se crea aquí con esa misma definición.
- Empresas del grupo con planes de cuentas o reglas de costos distintos: la suma mezcla criterios. Mostrarlo.
- Comercial: un «grupo» puede ser un mismo dueño con varios NIT o instituciones independientes; el contrato debe distinguirlo.
