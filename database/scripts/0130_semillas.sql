/* 0130 · Población de dim.Fecha y semillas. */
/* ============================ dim.Fecha 2015-2035 ============================ */
WITH n AS (
    SELECT TOP (DATEDIFF(DAY, '2015-01-01', '2035-12-31') + 1)
           ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT dim.Fecha (FechaKey, Fecha, Anio, Trimestre, Mes, PeriodoKey, NombreMes, SemanaIso, DiaSemana, EsFinDeMes, EsHabil)
SELECT YEAR(x.d) * 10000 + MONTH(x.d) * 100 + DAY(x.d), x.d, YEAR(x.d), DATEPART(QUARTER, x.d), MONTH(x.d),
       YEAR(x.d) * 100 + MONTH(x.d),
       CHOOSE(MONTH(x.d), N'Enero', N'Febrero', N'Marzo', N'Abril', N'Mayo', N'Junio', N'Julio', N'Agosto', N'Septiembre', N'Octubre', N'Noviembre', N'Diciembre'),
       DATEPART(ISO_WEEK, x.d), x.dow,
       CASE WHEN x.d = EOMONTH(x.d) THEN 1 ELSE 0 END,
       CASE WHEN x.dow <= 5 THEN 1 ELSE 0 END
FROM n
CROSS APPLY (SELECT DATEADD(DAY, n.i, CAST('2015-01-01' AS date)) AS d) d0
CROSS APPLY (SELECT d0.d AS d, (DATEPART(WEEKDAY, d0.d) + @@DATEFIRST - 2) % 7 + 1 AS dow) x;
GO

/* ============================ ROLES DE USUARIO (Identity, D27) ============================ */
INSERT seg.Rol (Name, NormalizedName, ConcurrencyStamp)
SELECT r.Nombre, r.Nombre, CONVERT(nvarchar(36), NEWID())
FROM (VALUES ('SUPERADMIN'),('ADMIN_EMPRESA'),('GERENTE'),('FINANCIERO'),('CARTERA'),('TESORERO'),('FARMACIA'),('AUDITOR'),('CONSULTA')) r (Nombre)
WHERE NOT EXISTS (SELECT 1 FROM seg.Rol x WHERE x.NormalizedName = r.Nombre);
GO

/* ============================ MATRIZ ROL × PERMISO (D35, [S] hasta que el dueño la confirme) ============================
   Catálogo de permisos (24 §4.1):
     dash.<modulo>      ver los dashboards del módulo (además, el módulo debe estar activo para la empresa)
     reportes.exportar  generar PDF/Excel en segundo plano
     hallazgos.gestionar cambiar el estado de hallazgos (auditor y validador)
     ia.usar            usar el asistente (además, ia.RolPermitido de la empresa)
     admin.usuarios     crear, editar, desactivar y restablecer usuarios de su empresa
     admin.empresas, admin.ia, admin.catalogos   solo superadmin                                                       */
DECLARE @p TABLE (Rol varchar(20), Permiso varchar(60));
DECLARE @dash TABLE (Permiso varchar(60));
INSERT @dash VALUES ('dash.financiero'),('dash.ciclo_ingreso'),('dash.cartera'),('dash.tesoreria'),('dash.glosas'),('dash.agenda'),
    ('dash.diagnostico'),('dash.hospitalario'),('dash.farmacia'),('dash.inventario'),('dash.nomina_costos'),('dash.ese_presupuesto'),('dash.auditor_config');
-- SUPERADMIN: todo
INSERT @p SELECT 'SUPERADMIN', Permiso FROM @dash;
INSERT @p VALUES ('SUPERADMIN','reportes.exportar'),('SUPERADMIN','hallazgos.gestionar'),('SUPERADMIN','ia.usar'),('SUPERADMIN','admin.usuarios'),
                 ('SUPERADMIN','admin.empresas'),('SUPERADMIN','admin.ia'),('SUPERADMIN','admin.catalogos');
-- ADMIN_EMPRESA y GERENTE: todos los dashboards (nómina solo para gerencia, 05 §6b)
INSERT @p SELECT r.Rol, d.Permiso FROM @dash d CROSS JOIN (VALUES ('ADMIN_EMPRESA'),('GERENTE')) r (Rol);
INSERT @p VALUES ('ADMIN_EMPRESA','reportes.exportar'),('ADMIN_EMPRESA','hallazgos.gestionar'),('ADMIN_EMPRESA','ia.usar'),('ADMIN_EMPRESA','admin.usuarios'),
                 ('GERENTE','reportes.exportar'),('GERENTE','ia.usar');
INSERT @p VALUES
 ('FINANCIERO','dash.financiero'),('FINANCIERO','dash.ciclo_ingreso'),('FINANCIERO','dash.cartera'),('FINANCIERO','dash.tesoreria'),
 ('FINANCIERO','dash.glosas'),('FINANCIERO','dash.ese_presupuesto'),('FINANCIERO','dash.inventario'),('FINANCIERO','reportes.exportar'),('FINANCIERO','ia.usar'),
 ('CARTERA','dash.cartera'),('CARTERA','dash.glosas'),('CARTERA','dash.ciclo_ingreso'),('CARTERA','reportes.exportar'),
 ('TESORERO','dash.tesoreria'),('TESORERO','dash.cartera'),('TESORERO','reportes.exportar'),
 ('FARMACIA','dash.farmacia'),('FARMACIA','dash.inventario'),('FARMACIA','hallazgos.gestionar'),('FARMACIA','reportes.exportar'),
 ('AUDITOR','dash.auditor_config'),('AUDITOR','hallazgos.gestionar'),('AUDITOR','reportes.exportar');
-- CONSULTA: solo lectura de dashboards, sin nómina, sin exportar, sin IA
INSERT @p SELECT 'CONSULTA', Permiso FROM @dash WHERE Permiso <> 'dash.nomina_costos';

INSERT seg.RolPermiso (RolId, Permiso)
SELECT r.Id, p.Permiso
FROM @p p JOIN seg.Rol r ON r.NormalizedName = p.Rol
WHERE NOT EXISTS (SELECT 1 FROM seg.RolPermiso x WHERE x.RolId = r.Id AND x.Permiso = p.Permiso);
GO

/* ============================ PLAN DE IA POR DEFECTO ============================
   ia.EmpresaConfig exige PlanId y ModeloPrincipalId: el alta de empresa (spec 22 §4) usa este plan y ia.Parametro.MODELO_DEFECTO.
   [S] Cupo 0 hasta definir los planes comerciales; la IA es solo interna en la fase 1 (§5.7) y nace deshabilitada. */
INSERT ia.PlanComercial (Codigo, Nombre, CupoIncluidoCop)
SELECT 'PILOTO', N'Piloto (sin cupo de IA)', 0
WHERE NOT EXISTS (SELECT 1 FROM ia.PlanComercial WHERE Codigo = 'PILOTO');
GO
