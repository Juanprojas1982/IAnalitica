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
CREATE TABLE seg.UsuarioGrupo (UsuarioId int NOT NULL REFERENCES seg.Usuario(Id), GrupoId int NOT NULL REFERENCES seg.Grupo, PRIMARY KEY (UsuarioId, GrupoId));
```
- `seg.Grupo` no tiene `EmpresaId` → queda fuera de la RLS automática: lectura solo por la aplicación con verificación explícita; escritura solo superadmin (`admin.empresas`). `seg.GrupoEmpresa` **sí** tiene `EmpresaId` y entra en la política: revisar que el predicado no impida al servicio de grupo leer la membresía (leerla con contexto de superadmin técnico o excluir la tabla con una política propia; decidir en implementación con prueba).
- Permiso nuevo `grupo.ver` (script de semillas nuevo; amplía P10).

### Application `Grupos/`
- `ObtenerMetricaGrupoQuery(GrupoId, Metrica, Filtros)`: valida que el usuario esté en `seg.UsuarioGrupo`; obtiene las empresas del grupo; para cada una crea un **alcance de sesión de sistema** con `EmpresaId = n` (mismo mecanismo que necesita PL-04 para alertas) y ejecuta `IMotorMetricas` sin dimensiones o con `periodo`; devuelve `{empresa, valor}` + total.
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
- Usuario de grupo **no** puede leer filas de B por ningún tablero normal ni por el asistente.
- Quitar una empresa del grupo corta el acceso en la siguiente consulta (sin esperar caché).
- Total = suma de las empresas para una métrica aditiva; una razón no se totaliza.
- La consulta queda auditada en cada empresa.

## Riesgos
- La sesión de sistema por empresa es un mecanismo potente: debe ser interno, no inyectable desde un endpoint, y con pruebas propias. Si PL-04 ya lo creó, reutilizarlo.
- Empresas del grupo con planes de cuentas o reglas de costos distintos: la suma mezcla criterios. Mostrarlo.
- Comercial: un «grupo» puede ser un mismo dueño con varios NIT o instituciones independientes; el contrato debe distinguirlo.
