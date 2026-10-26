# Spec: Conector SaludIPS → Plataforma analítica

Estado: diseño (Opus). Implementación: Sonnet, en la solución `SaludIPS.sln`.
Relacionados: `05_contrato_api_ingesta.md` (API), `06_extraccion_saludips.sql` y `11_extraccion_operativa_saludips.sql` (consultas).

## 1. Qué es
Un proyecto nuevo **dentro de SaludIPS** que cada noche lee la BD de la IPS y envía los datos a la API de ingesta de la plataforma. Su URL, credenciales, endpoints y comportamiento se configuran desde los **parámetros de SaludIPS**.
- Solo hace **conexiones de salida HTTPS**. No abre puertos en el servidor de la IPS.
- Solo **lee** la BD, con un login restringido.
- **Ningún dato de paciente sale identificable.** El hash se calcula con un secreto que nunca sale de la IPS.

## 2. Decisiones de diseño
| Tema | Decisión | Razón |
|---|---|---|
| Proyecto | `SaludIPSNet.Analitica.Conector`, Worker Service con `Microsoft.NET.Sdk.Worker` sobre **net48** y `Microsoft.Extensions.Hosting.WindowsServices` | Sigue el patrón de `SaludIPSNet.Conexia` y `SaludIPSNet.SIFacturaElectronica.WindowsService`; reutiliza `SaludIPSNet.Entities` y `BLL` (EF6/net48) y se instala con SaludIPS |
| «Usuario y contraseña» | Son el **ClientId y el ClientSecret** de OAuth2 *client credentials* que entrega la plataforma (OpenIddict + `seg.ApiClienteEmpresa`) | Funciona igual para el usuario, pero el secreto se puede rotar y revocar por empresa |
| Dónde viven los parámetros | `SIIFNet.Parametros`, declarados en `SaludIPSNet.Entities/Adds/Parametros.cs` (región `Analitica`), igual que los otros 354 parámetros | Mecanismo existente, con interfaz y caché |
| Consultas | **Funciones de tabla en línea** `analitica.fn_*(@Desde, @Hasta, …)` dentro de la BD de SaludIPS, desplegadas con `SQLServerScriptsRunner` y versionadas con cada release | Si cambia el esquema de SaludIPS, la función cambia en el mismo release y el contrato no se rompe |
| Login de BD | `analitica_lector` con `SELECT` **solo** sobre el esquema `analitica`. Por encadenamiento de propiedad (mismo dueño `dbo`) lee las tablas sin tener permiso directo sobre ellas | Mínimo privilegio |
| Registro | NLog, como `SaludIPSNet.Tipificacion.Worker`, más una tabla local de ejecuciones | Trazable sin depurador |

## 3. Parámetros (`Parametros.cs`, región `Analitica`)
| Código | Valor por defecto | Descripción |
|---|---|---|
| `Analitica.Habilitado` | `NO` | Activa el envío |
| `Analitica.UrlBase` | *(vacío)* | URL de la API de la plataforma (`https://…`) |
| `Analitica.UrlToken` | `/connect/token` | Endpoint de token |
| `Analitica.ClientId` | *(vacío)* | «Usuario» entregado por la plataforma |
| `Analitica.ClientSecret.Secreto` | *(vacío)* | «Contraseña», guardada **cifrada** (§5) |
| `Analitica.Sal.Secreto` | *(se genera al activar)* | Secreto para anonimizar pacientes. Nunca se envía |
| `Analitica.HoraEjecucion` | `23:00` | Hora local de inicio |
| `Analitica.DesfaseMaxMinutos` | `60` | Desfase aleatorio para no saturar la plataforma con 50 IPS a la misma hora |
| `Analitica.MesesMaxVentana` | `13` | Tope de la ventana diaria |
| `Analitica.TamanoLote` | `5000` | Filas por lote |
| `Analitica.TimeoutSegundos` | `120` | Timeout por llamada HTTP |
| `Analitica.Reintentos` | `4` | Reintentos con espera exponencial (2, 4, 8, 16 s) |
| `Analitica.ModoSimulacion` | `NO` | `SI`: escribe los JSON en disco en lugar de enviarlos (validación y auditoría por la IPS) |
| `Analitica.RutaSimulacion` | `C:\SaludIPS\Analitica\Simulacion` | Carpeta de salida del modo simulación |
| `Analitica.Entidad.<Entidad>.Habilitada` | `SI` / `NO` | Activa o desactiva cada entidad (por ejemplo, glosas solo si se usa el módulo) |

**Riesgo detectado: los parámetros son por vigencia.** `ParametrosBLL` guarda y cachea los parámetros por `VigenciaTrabajo`, y `GetParametroByCodigo` crea el valor por defecto si no existe. Si al cambiar de año los parámetros no se copian a la nueva vigencia, **el 1 de enero el conector queda con `Habilitado = NO` y sin credenciales**. Antes de implementar, verificar el proceso de cambio de vigencia (`ConfiguracionesVigencia`). Si no copia, el conector debe leer la vigencia anterior cuando la actual no tiene el parámetro y avisarlo en la interfaz.

## 4. Entidades, orden y endpoints por defecto
| # | Entidad (nombre en `POST /v1/cargas`) | Nombre | Modo | Función SQL |
|---|---|---|---|---|
| 1 | Catálogos (sedes, CC, cuentas, comprobantes, terceros, pagadores, contratos, servicios, artículos, lotes, almacenes, profesionales, especialidades, usuarios, cuentas bancarias) | `<catalogo>` | COMPLETA (terceros INCREMENTAL) | `analitica.fn_Cat*` |
| 2 | Asientos contables | `asientos` | VENTANA | `analitica.fn_Asientos` |
| 3 | Facturas | `facturas` | VENTANA + INCREMENTAL de anulaciones | `analitica.fn_Facturas` |
| 4 | Recibos | `recibos` | VENTANA | `analitica.fn_Recibos` |
| 5 | Inventario | `inventario` | VENTANA | `analitica.fn_Inventario` |
| 6 | Conciliaciones | `conciliaciones` | VENTANA | `analitica.fn_Conciliaciones` |
| 7 | Glosas | `glosas` | VENTANA | `analitica.fn_Glosas` |
| 8 | Agenda | `agenda-capacidad` | VENTANA (incluye la agenda futura) | `analitica.fn_AgendaCapacidad` |
| 9 | Citas | `citas` | INCREMENTAL | `analitica.fn_Citas` |
| 10 | Prestaciones | `prestaciones` | INCREMENTAL | `analitica.fn_Prestaciones` |
| 11 | Estudios diagnósticos | `estudios` | INCREMENTAL | `analitica.fn_Estudios` |
| 12 | Nómina | `nomina` | VENTANA por periodo | `analitica.fn_Nomina` |
| 13 | Envíos FE y RIPS | `envios` | INCREMENTAL | `analitica.fn_Envios` |
| 14 | Autorizaciones | `autorizaciones` | INCREMENTAL | `analitica.fn_Autorizaciones` |
| 15 | Admisiones pendientes | `admisiones-pendientes` | FOTO | `analitica.fn_AdmisionesPendientes` |

**Envío:** todas las entidades usan el mismo flujo `POST /v1/cargas` → lotes → cerrar (24_SPEC_MAESTRA §7.1); la entidad va en el cuerpo. No hay una URL por entidad.

**Ventana diaria:** desde el primer día del periodo contable abierto más antiguo (`SIIFNet.CierresContables`) hasta hoy, con un tope de `MesesMaxVentana`. Los incrementales se calculan desde la última carga correcta que reporta `GET /v1/estado`, menos 2 días de margen.

## 5. Secretos
- **Problema:** el `Cipher` existente (`SaludIPSNet.BLL/Adds/EncryptRepository.cs`) usa AES con la clave y el IV derivados de la misma contraseña. Así, el mismo texto produce siempre el mismo cifrado. Es aceptable para datos internos, **no para credenciales**.
- **Propuesta:** guardar los parámetros terminados en `.Secreto` cifrados con **DPAPI** (`ProtectedData`, alcance `LocalMachine`, con entropía propia del conector) y el prefijo `DPAPI:`.
  - Funciona si la web y el conector corren en el **mismo servidor**, que es la instalación típica de SaludIPS.
  - Si están en servidores distintos, usar AES‑GCM con una clave en una variable de entorno del servidor del conector (`SALUDIPS_ANALITICA_KEY`), nunca en la BD.
- En la pantalla de parámetros, los `.Secreto` se muestran enmascarados (`••••`) y solo se reemplazan, nunca se leen.

## 6. Pantalla en SaludIPS (área SIIFNet: «Integración analítica»)
- **Configuración:** los parámetros de §3, con validación de URL HTTPS.
- **Probar conexión:** obtiene un token y llama `GET /v1/estado`. Muestra la empresa reconocida por la plataforma y la última carga correcta por entidad.
- **Ejecutar ahora:** todas las entidades o una, con una ventana opcional. Sirve para la carga inicial por meses.
- **Monitor:** las últimas ejecuciones con entidad, ventana, filas, rechazos, duración y error, con enlace a los rechazos de la plataforma.
- **Vista previa:** el modo simulación y la descarga del JSON de una entidad, para que la IPS vea **exactamente** qué sale de su servidor.
- **Permiso:** una constante nueva en `SIIFNet.Permisos` («Integración analítica»), solo para administradores.

## 7. Flujo de ejecución
```
Esperar HoraEjecucion + desfase aleatorio
  └─ Habilitado = NO → dormir
  └─ Token (client credentials) → GET /v1/estado (marcas de agua por entidad)
  └─ Para cada entidad habilitada, en orden:
       calcular la ventana o el incremental
       POST /v1/cargas                     (entidad, modo, ventana, versionAgente, versionFuente)
       SqlDataReader sobre analitica.fn_X → lotes de TamanoLote → JSON gzip → POST /lotes/{n}
       POST /cerrar con la suma de control
       consultar el estado hasta OK, ERROR o timeout
       registrar en AnaliticaEjecucion
  └─ Fallo de una entidad: se registra y se sigue con las independientes.
     Si fallan los catálogos, no se envían hechos.
```
- **Streaming:** nunca carga una entidad completa en memoria. Lee y serializa por lote.
- **Idempotencia:** cada lote lleva su número; reintentar un lote no duplica. Si el proceso cae, la siguiente ejecución recalcula la ventana.
- `versionFuente` = `cAdministracion.Version` y `FechaVersion` de SaludIPS. La plataforma puede rechazar versiones incompatibles.
- Usar TLS 1.2 o superior; el `ServicePointManager` de net48 se fija explícitamente, como ya lo hace `Conexia`.

## 8. Cambios en la BD de SaludIPS (script en `SQLServerScriptsRunner`)
```sql
CREATE SCHEMA analitica;
-- una función de tabla en línea por entidad, con el SQL de 06 y 11 (ej. analitica.fn_Asientos(@Desde date, @Hasta date))
CREATE TABLE analitica.Ejecucion (
    Id bigint IDENTITY PRIMARY KEY, Entidad varchar(40) NOT NULL, Modo varchar(12) NOT NULL,
    VentanaDesde date NULL, VentanaHasta date NULL, InicioEn datetime2(0) NOT NULL, FinEn datetime2(0) NULL,
    Estado varchar(12) NOT NULL, Filas int NULL, Rechazos int NULL, CargaIdRemota bigint NULL, Error nvarchar(2000) NULL);
CREATE USER analitica_lector FOR LOGIN analitica_lector;
GRANT SELECT ON SCHEMA::analitica TO analitica_lector;
GRANT INSERT, UPDATE ON analitica.Ejecucion TO analitica_lector;   -- solo su bitácora
```

## 9. Pruebas
- **Unitarias** en `SaludIPS.Tests`: cálculo de ventanas (cambio de año, periodo cerrado), partición en lotes, serialización decimal con punto y sin notación científica, reintentos y lectura de secretos.
- **Integración:** contra un ambiente de pruebas de la plataforma con una copia anonimizada de una IPS, más el cuadre de los totales enviados contra los reportes de SaludIPS (`ESTADORESULTADOSCENTROCOSTOHORIZONTAL`, `CARTERAESTADOCUENTAEDADES`, `SIPS_5`).
- **Carga inicial** de 13 meses en una IPS real, midiendo tiempo, uso de CPU de la BD y tamaño enviado. Se ejecuta fuera de horario.

## 10. Pendientes por validar
1. La copia de parámetros entre vigencias (§3).
2. Si la web y el servicio corren en el mismo servidor en todas las instalaciones (DPAPI).
3. Los campos marcados **[S]** en `06` y `11`.
4. El impacto de la carga nocturna en IPS que trabajan 24 h (urgencias). Ejecutar con prioridad baja y lotes pequeños si hace falta.
