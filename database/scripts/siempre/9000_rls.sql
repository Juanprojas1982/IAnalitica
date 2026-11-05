/* 9000 · Row-Level Security (D35, D36). Se ejecuta en CADA despliegue (DbUp RunAlways), después de todos los scripts numerados:
   borra y regenera la política seg.PolTenant sobre TODAS las tablas con columna EmpresaId, así las tablas nuevas quedan cubiertas solas.

   Contexto de sesión (lo fija el interceptor en cada apertura de conexión, sp_set_session_context ... @read_only = 1):
     EmpresaId        int  empresa del usuario (portal) o del token (API). NULL para el superadmin.
     UsuarioId        int  usuario del portal. NULL en la API.
     EsSuperAdmin     bit  1 solo para el superadmin (ve todas las empresas, D11).
     EsAdminPrincipal bit  1 solo para el Admin{NIT} (ve la auditoría de su empresa).
     Autenticacion    bit  1 solo en el inicio de sesión, 2FA, cambio de clave y el endpoint de token: permite leer
                           seg.Usuario y seg.ApiClienteEmpresa antes de conocer la empresa. Ninguna otra ruta lo fija.
   rol_etl (worker de procesamiento) no tiene filtro. dbo y sysadmin SÍ quedan filtrados: las pruebas usan usuarios de rol_app.  */
IF OBJECT_ID('seg.PolTenant', 'SP') IS NOT NULL DROP SECURITY POLICY seg.PolTenant;
GO
-- Las funciones son SCHEMABINDING y se referencian entre sí: se borran de las dependientes a la base y se recrean.
DROP FUNCTION IF EXISTS seg.fn_FiltroIdentidad, seg.fn_FiltroEmpresaOGlobal, seg.fn_FiltroAuditoria, seg.fn_SoloSuperadmin, seg.fn_FiltroEmpresa;
GO
CREATE FUNCTION seg.fn_FiltroEmpresa (@EmpresaId int)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN
    SELECT 1 AS Permitido
    WHERE IS_MEMBER(N'rol_etl') = 1
       OR CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit) = 1
       OR @EmpresaId = CAST(SESSION_CONTEXT(N'EmpresaId') AS int);
GO
-- EmpresaId NULL = global (ia.Tarifa, cfg.Dashboard de fábrica): lectura para todos; escritura de lo global solo superadmin (BLOCK con fn_FiltroEmpresa).
CREATE FUNCTION seg.fn_FiltroEmpresaOGlobal (@EmpresaId int)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN
    SELECT 1 AS Permitido WHERE @EmpresaId IS NULL
    UNION ALL
    SELECT 1 FROM seg.fn_FiltroEmpresa(@EmpresaId);
GO
-- Auditoría: la de una empresa la ve su Admin{NIT}; la global (EmpresaId NULL), solo el superadmin.
CREATE FUNCTION seg.fn_FiltroAuditoria (@EmpresaId int)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN
    SELECT 1 AS Permitido
    WHERE IS_MEMBER(N'rol_etl') = 1
       OR CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit) = 1
       OR (@EmpresaId IS NOT NULL AND @EmpresaId = CAST(SESSION_CONTEXT(N'EmpresaId') AS int)
           AND CAST(SESSION_CONTEXT(N'EsAdminPrincipal') AS bit) = 1);
GO
CREATE FUNCTION seg.fn_SoloSuperadmin (@EmpresaId int)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN
    SELECT 1 AS Permitido
    WHERE IS_MEMBER(N'rol_etl') = 1 OR CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit) = 1;
GO
-- Identidad (seg.Usuario, seg.ApiClienteEmpresa): además, visible y actualizable durante la autenticación
-- (búsqueda por nombre, intentos fallidos, bloqueo, sello de seguridad).
CREATE FUNCTION seg.fn_FiltroIdentidad (@EmpresaId int)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN
    SELECT 1 AS Permitido WHERE CAST(SESSION_CONTEXT(N'Autenticacion') AS bit) = 1
    UNION ALL
    SELECT 1 FROM seg.fn_FiltroEmpresa(@EmpresaId);
GO
DECLARE @sql nvarchar(max);
WITH t AS (
    SELECT QUOTENAME(s.name) + N'.' + QUOTENAME(o.name) AS Tabla, s.name + N'.' + o.name AS Nombre
    FROM sys.tables o
    JOIN sys.schemas s ON s.schema_id = o.schema_id
    JOIN sys.columns c ON c.object_id = o.object_id AND c.name = N'EmpresaId'
    WHERE s.name IN (N'seg', N'cfg', N'etl', N'stg', N'dim', N'fact', N'ia', N'rpt', N'reg', N'saludips', N'aud')
),
p AS (   -- predicados por tabla
    SELECT CASE
             WHEN Nombre IN (N'ia.Tarifa', N'cfg.Dashboard') THEN
                 N'ADD FILTER PREDICATE seg.fn_FiltroEmpresaOGlobal(EmpresaId) ON ' + Tabla
               + N', ADD BLOCK PREDICATE seg.fn_FiltroEmpresa(EmpresaId) ON ' + Tabla + N' AFTER INSERT'
               + N', ADD BLOCK PREDICATE seg.fn_FiltroEmpresa(EmpresaId) ON ' + Tabla + N' AFTER UPDATE'
               + N', ADD BLOCK PREDICATE seg.fn_FiltroEmpresa(EmpresaId) ON ' + Tabla + N' BEFORE UPDATE'
               + N', ADD BLOCK PREDICATE seg.fn_FiltroEmpresa(EmpresaId) ON ' + Tabla + N' BEFORE DELETE'
             WHEN Nombre = N'aud.AccesoSuperadmin' THEN
                 N'ADD FILTER PREDICATE seg.fn_SoloSuperadmin(EmpresaId) ON ' + Tabla
             WHEN Nombre LIKE N'aud.%' THEN   -- solo inserción por permisos; sin BLOCK: hay eventos antes de iniciar sesión
                 N'ADD FILTER PREDICATE seg.fn_FiltroAuditoria(EmpresaId) ON ' + Tabla
             WHEN Nombre IN (N'seg.Usuario', N'seg.ApiClienteEmpresa') THEN
                 N'ADD FILTER PREDICATE seg.fn_FiltroIdentidad(EmpresaId) ON ' + Tabla
               + N', ADD BLOCK PREDICATE seg.fn_FiltroIdentidad(EmpresaId) ON ' + Tabla + N' AFTER INSERT'
               + N', ADD BLOCK PREDICATE seg.fn_FiltroIdentidad(EmpresaId) ON ' + Tabla + N' AFTER UPDATE'
             ELSE
                 N'ADD FILTER PREDICATE seg.fn_FiltroEmpresa(EmpresaId) ON ' + Tabla
               + N', ADD BLOCK PREDICATE seg.fn_FiltroEmpresa(EmpresaId) ON ' + Tabla + N' AFTER INSERT'
               + N', ADD BLOCK PREDICATE seg.fn_FiltroEmpresa(EmpresaId) ON ' + Tabla + N' AFTER UPDATE'
           END AS Predicados
    FROM t
)
SELECT @sql = N'CREATE SECURITY POLICY seg.PolTenant ' + STRING_AGG(CAST(Predicados AS nvarchar(max)), N', ')
            + N' WITH (STATE = ON, SCHEMABINDING = ON);'
FROM p;
EXEC sys.sp_executesql @sql;
GO
