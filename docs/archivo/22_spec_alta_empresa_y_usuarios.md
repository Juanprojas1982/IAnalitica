# Spec: Alta de empresas y gestión de usuarios

Estado: diseño (Opus). Implementación: Sonnet. Autenticación: ASP.NET Core Identity (D12).

## 1. Reglas de negocio (tuyas)
1. **Solo el superadmin** (administrador general) crea empresas.
2. Al crear una empresa se crea automáticamente su **usuario administrador principal**, con nombre de usuario **`Admin{NIT}`** (ej. `Admin900987452`, NIT sin dígito de verificación ni guiones).
3. El superadmin entrega ese usuario y la contraseña inicial a la empresa.
4. El administrador de la empresa cambia la contraseña y **crea los usuarios que quiera, solo de su empresa**, con los roles disponibles.

## 2. Contraseña inicial: objeción de seguridad y propuesta
**Tu propuesta:** contraseña fija `Admin123` para todas las empresas.
**Problema:**
- El NIT es público. Cualquiera que conozca el patrón `Admin{NIT}` + `Admin123` puede entrar a una empresa **antes** de que cambie la clave, y ver datos financieros y de su personal.
- Es un riesgo real y, con datos de salud, también contractual: el Anexo A promete «medidas de seguridad razonables».
- Además, `Admin123` **no cumple** la política por defecto de Identity, que exige un carácter especial.

**Propuesta: mismo flujo, sin el riesgo.**
| Aspecto | Propuesta |
|---|---|
| Usuario | `Admin{NIT}` (se mantiene) |
| Contraseña inicial | **Generada al azar** (14 caracteres, por ejemplo `Kq7#vR2m@Lp9xT`), **distinta por empresa**. Se muestra **una sola vez** al superadmin en pantalla, con un botón para copiar, y nunca se guarda en claro |
| Entrega | El superadmin la envía por un canal distinto al del usuario. Por ejemplo, el usuario por correo y la contraseña por WhatsApp o llamada al representante |
| Primer ingreso | **Obligatorio cambiar la contraseña** y **configurar el doble factor** (app autenticadora) antes de ver cualquier dato |
| Caducidad | La contraseña inicial **vence a las 72 h**. Si no se usó, el superadmin la regenera con un clic |
| Bloqueo | 5 intentos fallidos → bloqueo de 15 min y alerta en `aud.Seguridad` |

**Decisión (D26): contraseña aleatoria.** La alternativa siguiente queda descartada y solo se conserva como registro:

~~Si aun así prefieres `Admin123`:~~ mínimo obligatorio:
- (a) cambio de contraseña en el primer ingreso;
- (b) la cuenta **nace desactivada** y el superadmin la activa en el momento de entregarla;
- (c) caducidad de 24 h;
- (d) doble factor en el primer ingreso;
- (e) excepción explícita en la política de contraseñas solo para la contraseña inicial.

Queda registrado como **riesgo aceptado por ti** en `24_SPEC_MAESTRA.md §17`.

## 3. Modelo (Identity)
```csharp
// IAnalitica.Infrastructure/Identity/UsuarioAplicacion.cs
public sealed class UsuarioAplicacion : IdentityUser<int>
{
    public int? EmpresaId { get; set; }                 // null solo para el superadmin
    public bool EsSuperAdmin { get; set; }
    public string NombreCompleto { get; set; } = "";
    public bool DebeCambiarClave { get; set; }          // true al crear o restablecer
    public DateTime? ClaveTemporalVenceEn { get; set; }
    public bool EsAdminPrincipal { get; set; }          // el Admin{NIT}: no se puede eliminar ni degradar
    public bool Activo { get; set; } = true;
    public DateTime CreadoEn { get; set; }
    public int? CreadoPor { get; set; }
}
```
- Reemplaza la tabla `seg.Usuario` del diseño v2. Se mantienen la restricción «superadmin sin empresa / usuario con una empresa» ; la RLS se rediseñó en `24_SPEC_MAESTRA.md` §4.2 (D35).
- Roles: `SUPERADMIN`, `ADMIN_EMPRESA`, `GERENTE`, `FINANCIERO`, `CARTERA`, `TESORERO`, `FARMACIA`, `AUDITOR`, `CONSULTA`, con permisos por rol (`seg.RolPermiso`).

## 4. Flujo de alta de empresa (superadmin)
1. **Formulario:** NIT (se valida el DV con el cálculo módulo 11 de la DIAN), razón social, tipo (IPS, ESE, operador farmacéutico), contactos, logo, colores, plan, cupo de IA y módulos activos.
2. **Transacción única:**
   - crea `seg.Empresa`, `ia.EmpresaConfig` (deshabilitada hasta el consentimiento, si aplica) y `seg.EmpresaModulo`;
   - crea el usuario `Admin{NIT}` con rol `ADMIN_EMPRESA`, `EsAdminPrincipal = true` y `DebeCambiarClave = true`;
   - crea la credencial de API del conector (ClientId y secreto **mostrado una sola vez**);
   - escribe todo en `aud.CambioAdministrativo`.
3. **Pantalla final («kit de entrega»):** usuario, contraseña inicial (una vez), URL del portal, ClientId y secreto del conector (una vez), e instrucciones de primer ingreso. Botón «Copiar» y aviso de no guardarlo en correos sin protección.
4. **Validaciones:** NIT único (si existe, no se crea); el nombre `Admin{NIT}` no puede existir.

## 5. Gestión de usuarios por el administrador de la empresa
- Crea, edita, desactiva y restablece usuarios **solo de su empresa**. El `EmpresaId` lo pone el servidor desde la sesión, nunca el formulario.
- Asigna roles de la lista permitida. **No puede** crear superadmins ni asignar el rol `ADMIN_EMPRESA`: **solo existe un administrador por empresa, el `Admin{NIT}`** (D27). Esto se valida en el servidor y además con el índice único `UX_Usuario_AdminPrincipal` (`EmpresaId` donde `EsAdminPrincipal = 1`).
- **Reemplazo del administrador** (por ejemplo, si la persona deja la empresa): solo el superadmin. Restablece la contraseña del `Admin{NIT}` con una contraseña temporal nueva, quita el doble factor anterior para que la nueva persona lo configure, y la acción queda en `aud.CambioAdministrativo` con su motivo.
- Usuario nuevo: contraseña temporal generada, cambio obligatorio y doble factor según el rol (obligatorio para los administradores, recomendado para el resto).
- No se borran usuarios: se **desactivan**, para conservar la auditoría.
- El `Admin{NIT}` no se puede desactivar desde la empresa. Si hay que reemplazarlo, lo hace el superadmin.

## 6. Seguridad complementaria
- Política de contraseñas: mínimo 10 caracteres, con mayúscula, minúscula, número y carácter especial; sin repetir las últimas 5 (requiere una tabla de historial, porque Identity no lo trae).
- Bloqueo por intentos, alertas por inicios desde IP o país nuevo y cierre de sesión por inactividad (por ejemplo, 30 min).
- Todo cambio de usuarios y roles queda en `aud.CambioAdministrativo` con el antes y el después.
- **El superadmin no puede leer contraseñas.** Solo puede restablecerlas, lo que genera una nueva contraseña temporal y fuerza el cambio.
