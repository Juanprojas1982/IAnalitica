# IAnalitica

Plataforma de analítica gerencial para el sector salud.

**Punto de entrada:** [`docs/24_SPEC_MAESTRA.md`](docs/24_SPEC_MAESTRA.md) — especificación maestra (precedencia, arquitectura, fases y decisiones).
Notas de la implementación de F0: [`docs/25_NOTAS_IMPLEMENTACION_F0.md`](docs/25_NOTAS_IMPLEMENTACION_F0.md).

## Requisitos locales
- .NET SDK 10
- Docker (las pruebas levantan SQL Server 2022 en contenedor con Testcontainers; no se usa ningún servicio de la nube)

## Compilar y probar
```bash
dotnet build            # TreatWarningsAsErrors: una advertencia rompe la compilación
dotnet test             # unitarias + integración (contenedor SQL Server) + aislamiento entre empresas
```

## Correr el portal en local
```bash
# 1. SQL Server local (cualquier instancia). Ejemplo con Docker:
docker run -d --name sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<clave>' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest

# 2. Migrar (crea la base con colación Modern_Spanish_CI_AS y aplica database/scripts)
export IANALITICA_CONEXION='Server=localhost,1433;Database=IAnalitica;User Id=sa;Password=<clave>;TrustServerCertificate=True'
dotnet run --project database/IAnalitica.Database.Migrator -- migrar --asegurar-bd

# 3. Primer superadmin (la contraseña inicial se muestra UNA vez)
dotnet run --project database/IAnalitica.Database.Migrator -- crear-superadmin --usuario superadmin --correo yo@ejemplo.co

# 4. Usuario de BD de la aplicación (rol_app, sujeto a RLS) y el portal
#    CREATE LOGIN app_local WITH PASSWORD='<clave>'; CREATE USER app_local FOR LOGIN app_local; ALTER ROLE rol_app ADD MEMBER app_local;
cd src/IAnalitica.Web
dotnet user-secrets set "ConnectionStrings:App" "Server=localhost,1433;Database=IAnalitica;User Id=app_local;Password=<clave>;TrustServerCertificate=True"
dotnet run
```
Las cadenas de conexión y los secretos van en User Secrets (local) o Key Vault (Azure); nunca en `appsettings` ni en el repositorio.

## Estructura
`src/` capas y hosts (§3.2) · `database/scripts` DDL único (fuente de verdad, §6) · `database/IAnalitica.Database.Migrator` DbUp · `tests/` · `docs/` especificaciones.
