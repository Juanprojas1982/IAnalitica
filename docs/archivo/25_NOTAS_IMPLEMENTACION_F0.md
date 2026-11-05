# Notas de implementación — F0 · Base

Complementa a `24_SPEC_MAESTRA.md` (que manda). Aquí queda lo que se implementó, lo que se supuso (`[S]`) y lo que necesita decisión de diseño.

## 1. Qué incluye F0
| Entregable (§13) | Dónde | Cómo se verifica |
|---|---|---|
| Solución .NET 10 (§3.2) | `IAnalitica.sln`, `Directory.Build.props` (`TreatWarningsAsErrors`) | `dotnet build` sin advertencias |
| Mediador propio (§3.5) | `Application/Mediacion`, `Application/Behaviors` | `Application.Tests/Mediacion`: las 7 pruebas obligatorias + behaviors |
| Migrador DbUp + scripts | `database/IAnalitica.Database.Migrator`, `database/scripts` | `BaseDeDatosFixture` aplica todo en SQL Server 2022 (Testcontainers) |
| Identity + 2FA | `Infrastructure/Identity`, portal `Web/Components/Pages/Cuenta` | `IdentidadTests`, `PortalTests` (TOTP real, RFC 6238) |
| Alta de empresa, `Admin{NIT}`, kit de entrega (spec 22) | `Application/Empresas`, `Web/.../NuevaEmpresa.razor` | `AltaEmpresaTests`, `PortalTests` |
| Gestión de usuarios y reemplazo del admin (spec 22 §5) | `Application/Usuarios` | `GestionUsuariosTests` |
| RLS + pruebas de aislamiento | `database/scripts/siempre/9000_rls.sql`, `Infrastructure/Persistencia/ContextoBd.cs` | `AislamientoEntreEmpresasTests` (14) + `PortalTests` |
| OpenTelemetry local (spec 19) | `Infrastructure/Observabilidad` | `ObservabilidadTests` (limpieza, muestreo) |
| CI | `.github/workflows/ci.yml` | compila, verifica que el script 0005 coincide con EF y corre todas las pruebas |

**Criterio de aceptación (§13):** crear 2 empresas y que el `Admin{NIT}` de una no vea ni modifique nada de la otra → `AislamientoEntreEmpresasTests` y `PortalTests.Cada_Admin_ve_solo_los_usuarios...`.

## 2. Supuestos `[S]` usados
1. **Matriz rol × permiso** (24 §4.1, D37): propuesta mía, pendiente de confirmar (P10).
2. **Plan de IA `PILOTO` con cupo 0** (D38, P11).
3. **Nombre de usuario**: el `Admin{NIT}` es fijo; los demás usuarios los nombra el administrador (4–60 caracteres, `[A-Za-z0-9._-]`), **únicos en toda la plataforma** y sin el prefijo `Admin` (reservado). Esto implica que dos empresas no pueden tener el mismo nombre de usuario.
4. **`seg.Empresa.Codigo` = NIT** (la tabla exige un código único y la spec no define su formato).
5. **ClientId del conector** = `conector-{NIT}`.
6. **Límite de ingreso**: 20 solicitudes por minuto y por IP en las páginas de ingreso (Identity ya bloquea tras 5 intentos).
7. **Muestreo** (spec 19 §4): 100 % de los spans con error y una fracción de los exitosos, **por span y no por traza completa**: un muestreo por traza exige un colector.
8. **Procesador de limpieza**: enmascara por patrón números de 6 o más dígitos (cédulas, NIT, teléfonos). Es conservador: ante la duda enmascara.
9. **Doble factor por la empresa** (opcional para roles distintos del admin): el parámetro por empresa aún no existe; hoy solo es obligatorio para superadmin y `Admin{NIT}`.
10. **2FA sin QR**: el portal muestra la clave y el enlace `otpauth://` (evita agregar una librería). Un QR se puede agregar después.
11. **Llaves de Data Protection**: en una carpeta si existe `DataProtection:Ruta`; si no, efímeras (solo desarrollo). En Azure se decide con la infraestructura (F6).

## 3. Cambios al DDL respecto de los documentos fuente
Ver `24_SPEC_MAESTRA.md §6.1` (unificación) y además, durante F0:
- `seg.HistorialClave` recibe llave primaria `HistorialId` (EF Core la necesita para insertar).
- `etl.Trabajo.EmpresaId` admite NULL y se agrega `UsuarioId` (ya registrado en §6.1).

## 4. Hallazgos de las pruebas (corregidos)
- **Historial de contraseñas**: `UserManager.CreateAsync` usa una ruta privada, así que un override no lo registraba; ahora se guarda en `SaveChanges`.
- **Sello de seguridad**: `ResetAuthenticatorKeyAsync` lo renueva y tumbaba la sesión del propio usuario; la página lo reemite.
- **Formularios SSR**: los checkboxes sin marcar no se envían y un diccionario enlazado fallaba al mostrarse; se usan `<input>` nativos.
- **Validadores**: `WithErrorCode` solo aplica al último validador de la cadena; se separó una regla por cada código.

## 5. Para decidir (diseño)
1. **Render `InteractiveAuto`** (§9, §3.3): requiere un proyecto cliente WebAssembly (`IAnalitica.Web.Client`) que no figura en §3.2. F0 usa páginas estáticas del servidor; la decisión se necesita antes de F3 (dashboards).
2. **MudBlazor** (§3.3, §9): F0 usa Bootstrap de la plantilla. Se adopta MudBlazor con el tema por empresa en F3.
3. **Exportador a Azure Monitor** (spec 19 §2): no se agregó el paquete (D32, "paquete por confirmar"). El exportador activo es consola u OTLP; falta elegir la distribución de Azure Monitor y su límite de ingesta.
4. **Usuarios únicos globales** (supuesto 3): ¿se prefiere un sufijo por empresa para permitir el mismo nombre en empresas distintas?
5. **Licencia de Mapperly** (§3.3) y de `Blazor-ApexCharts`: pendientes de verificar al usarlos (no se usan en F0).
