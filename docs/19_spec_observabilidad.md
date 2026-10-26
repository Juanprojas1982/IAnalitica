# Spec: Logs, trazas, métricas y auditoría (OpenTelemetry)

Estado: diseño (Opus). Implementación: Sonnet. Decisión D22. Regla de costo D32.

## 1. Dos tipos de registro, dos destinos
| Tipo | Para qué | Dónde | Retención |
|---|---|---|---|
| **Telemetría técnica** (logs, trazas, métricas) | Diagnosticar fallas y rendimiento | **OpenTelemetry**, exportada a Azure Monitor (Application Insights / Log Analytics) | Corta (p. ej. 30 días) y muestreada |
| **Auditoría de negocio** | Quién vio, exportó o cambió qué; evidencia ante clientes y auditorías | Tablas SQL `aud.*` en la BD central (con RLS) | Larga (p. ej. 2 años, a confirmar con el abogado) |

Separarlos evita dos problemas: pagar ingesta de telemetría por datos de auditoría que deben conservarse años, y perder evidencia por el muestreo.

## 2. OpenTelemetry por componente
| Componente | Instrumentación | Nota |
|---|---|---|
| API y portal (.NET 10) | Distribución de Azure Monitor para OpenTelemetry en ASP.NET Core: trazas HTTP, `ILogger`, métricas | Paquete por confirmar al implementar |
| Worker (.NET 10) | `ActivitySource` propio por proceso: carga, cartera, distribución de costos, auditor, alertas | Una traza por carga |
| EF Core / SQL | Instrumentación de cliente SQL **sin el texto del comando con parámetros** | Evita datos sensibles |
| Llamadas a IA | Span por llamada con modelo, tokens, costo y resultado (**sin el texto** de la pregunta ni de la respuesta) | Cuadra con `ia.UsoLlamada` |
| **Conector SaludIPS (net48)** | OpenTelemetry .NET (compatible con .NET Framework) o, como mínimo, envío del encabezado **W3C `traceparent`** en cada llamada a la API | La traza va de punta a punta: conector → API → worker → BD |

**Identificadores comunes en cada span y log:** `empresa.id`, `carga.id`, `entidad`, `usuario.id` (interno, nunca correo ni nombre), `version.conector` y `version.saludips`.

## 3. Lo que nunca se registra (procesador de limpieza obligatorio)
- Cuerpos de las cargas (JSON de lotes), parámetros SQL y textos de preguntas o respuestas de IA.
- Secretos, tokens, ClientSecret, cadenas de conexión y encabezados `Authorization`.
- Nombres, documentos o correos de personas.

Un procesador de OpenTelemetry elimina o enmascara esos atributos antes de exportar. Hay pruebas unitarias que fallan si aparece un patrón de secreto o de documento.

## 4. Control de costo de la telemetría (D32)
- **Muestreo de trazas:** 100 % de los errores, 10 % de las solicitudes exitosas en producción y 100 % en desarrollo local, sin exportar a la nube.
- **Nivel de log:** `Warning` en producción; `Information` solo para los eventos de negocio clave (carga iniciada o terminada, regla ejecutada).
- **Métricas agregadas** en lugar de un log por fila.
- Límite diario de ingesta configurado en Log Analytics, con alerta al acercarse al cupo gratuito.
- Retención mínima. Lo que deba conservarse va a la auditoría SQL, no a la telemetría.

## 5. Auditoría de negocio (SQL)
| Tabla | Contenido |
|---|---|
| `aud.Acceso` (ya existe) | Ingreso, vista de dashboard y exportación |
| `aud.CambioAdministrativo` (nueva) | Cambio de tarifa de venta, plan, topes de IA, roles y permisos, configuración del conector, estado de hallazgos, consentimientos: **valor anterior, valor nuevo, usuario y fecha** |
| `aud.AccesoSuperadmin` (nueva) | Cada vez que el superadmin entra a ver datos de una empresa, con el motivo obligatorio |
| `aud.Seguridad` (nueva) | Inicios de sesión fallidos, bloqueo, activación o desactivación del doble factor, uso de credenciales de la API desde una IP nueva |

Las tablas `aud.*` son de solo inserción: el rol de la aplicación no tiene permiso de UPDATE ni DELETE.

## 6. Alertas operativas (Azure Monitor)
1. Empresa sin carga correcta a las 2:00 a.m.
2. Tasa de rechazos de una carga mayor que X %.
3. Errores 5xx en la API por encima del umbral.
4. Fallas de la IA o rechazos del proveedor.
5. Base de datos cerca del cupo gratuito o con la CPU sostenida alta.
6. Presupuesto de la suscripción al 50, 80 y 100 %.

Las alertas llegan por correo al equipo. Algunas, como la carga fallida, también al contacto técnico de la empresa.
