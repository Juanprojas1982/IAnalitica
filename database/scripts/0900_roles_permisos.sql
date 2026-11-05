/* 0900 · Roles de base de datos y permisos (D35). La RLS NO va aquí: siempre/9000_rls.sql se ejecuta en cada despliegue.
   Los logins/usuarios concretos los crea el ambiente, no DbUp:
     - Azure: usuarios contenidos de identidad administrada (CREATE USER [app-web] FROM EXTERNAL PROVIDER) + ALTER ROLE ... ADD MEMBER.
     - Pruebas: el fixture de Testcontainers crea un usuario por rol.
   rol_app : portal, API de ingesta y trabajos del worker que corren con el contexto de un usuario (reportes, IA). Sujeto a RLS.
   rol_etl : worker para el procesamiento de cargas y cálculos. Sin RLS (predicado). Nunca lo usa el portal ni la API.            */
IF DATABASE_PRINCIPAL_ID('rol_app') IS NULL CREATE ROLE rol_app;
IF DATABASE_PRINCIPAL_ID('rol_etl') IS NULL CREATE ROLE rol_etl;
GO
-- Negocio: lectura y escritura por esquema (cubre tablas futuras). Global (reg, dim globales, ia catálogos): la escritura la limita
-- la aplicación con los permisos admin.* (solo superadmin); la BD no distingue al superadmin por principal.
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::seg      TO rol_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::cfg      TO rol_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::etl      TO rol_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::stg      TO rol_app;
GRANT SELECT                         ON SCHEMA::dim      TO rol_app;
GRANT SELECT, UPDATE                 ON SCHEMA::fact     TO rol_app;   -- UPDATE: estado de hallazgos
GRANT SELECT                         ON SCHEMA::rpt      TO rol_app;
GRANT INSERT, UPDATE                 ON rpt.SolicitudReporte TO rol_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::ia       TO rol_app;
GRANT EXECUTE                        ON SCHEMA::ia       TO rol_app;   -- usp_ReservarCuota / usp_LiquidarReserva con el contexto del usuario
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::reg      TO rol_app;
GRANT SELECT                         ON SCHEMA::saludips TO rol_app;
GRANT SELECT, INSERT, UPDATE         ON SCHEMA::crm      TO rol_app;
GRANT SELECT, INSERT                 ON SCHEMA::aud      TO rol_app;
DENY  UPDATE, DELETE                 ON SCHEMA::aud      TO rol_app;   -- auditoría de solo inserción (spec 19 §5)

GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::etl      TO rol_etl;
GRANT SELECT, INSERT, UPDATE, DELETE          ON SCHEMA::stg      TO rol_etl;
GRANT SELECT, INSERT, UPDATE, DELETE          ON SCHEMA::dim      TO rol_etl;
GRANT SELECT, INSERT, UPDATE, DELETE          ON SCHEMA::fact     TO rol_etl;
GRANT SELECT, INSERT, UPDATE, DELETE          ON SCHEMA::saludips TO rol_etl;
GRANT SELECT, INSERT, UPDATE, DELETE          ON SCHEMA::reg      TO rol_etl;
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::ia       TO rol_etl;
GRANT SELECT                                  ON SCHEMA::seg      TO rol_etl;
GRANT SELECT                                  ON SCHEMA::cfg      TO rol_etl;
GRANT SELECT, INSERT, UPDATE                  ON SCHEMA::rpt      TO rol_etl;
GRANT SELECT, INSERT                          ON SCHEMA::aud      TO rol_etl;
DENY  UPDATE, DELETE                          ON SCHEMA::aud      TO rol_etl;
GRANT INSERT                                  ON seg.Notificacion TO rol_etl;
GO
