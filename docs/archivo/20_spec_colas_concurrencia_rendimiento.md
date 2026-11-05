# Spec: Colas, concurrencia y rendimiento

Estado: diseño (Opus). Implementación: Sonnet. Reglas D32 (costo) y D22 (OpenTelemetry).

## 1. Qué ya estaba y qué faltaba
| Ya definido | Faltaba |
|---|---|
| Ingesta por lotes de hasta 5.000 filas, idempotente por número de lote; procesamiento asíncrono después de `cerrar` | **Qué cola** se usa y cómo se procesa |
| Reserva atómica de cuota de IA (`ia.usp_ReservarCuota`) | **Concurrencia por empresa:** dos cargas de la misma entidad a la vez |
| Rate limiting por cliente de API y por usuario del chat | Reintentos, mensajes «venenosos» y orden de las tareas |
| Caché por empresa y versión de carga (mencionado) | Lecturas de los dashboards durante la carga nocturna |
| Desfase aleatorio de la hora de carga en el conector | Escalar el worker y evitar que 50 empresas saturen la BD |

## 2. Cola: abstracción + implementación barata
```csharp
// IAnalitica.Application/Abstracciones/IColaTrabajos.cs
public interface IColaTrabajos
{
    Task EncolarAsync(TrabajoPendiente trabajo, CancellationToken ct);
    Task<TrabajoTomado?> TomarSiguienteAsync(string tipoTrabajo, CancellationToken ct);  // con lease y visibilidad
    Task CompletarAsync(Guid trabajoId, CancellationToken ct);
    Task FallarAsync(Guid trabajoId, string error, CancellationToken ct);                 // reintento o "veneno"
}
```
| Etapa | Implementación | Costo | Por qué |
|---|---|---|---|
| **Pruebas y pilotos** | **Cola en SQL** (`etl.Trabajo`) dentro de la misma Azure SQL Database, con lectura `UPDLOCK, READPAST, ROWLOCK` | **$0 adicional** | 3 empresas y decenas de trabajos por noche; no justifica otro servicio |
| Crecimiento (decenas de empresas) | **Azure Storage Queues** detrás de la misma interfaz | Centavos al mes (verificar precio por operación antes de activarla, D32) | Desacopla la BD y permite autoescalar el worker con la cola de Container Apps |
| Solo si hace falta | Azure Service Bus (sesiones FIFO por empresa) | Más caro; requiere cotización | Orden estricto y sesiones; probablemente innecesario |

```sql
CREATE TABLE etl.Trabajo (
    TrabajoId     uniqueidentifier NOT NULL CONSTRAINT PK_Trabajo PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    Tipo          varchar(40) NOT NULL,        -- PROCESAR_CARGA, CALCULAR_CARTERA, DISTRIBUIR_COSTOS, AUDITAR_CONFIG, ALERTAS, INFORME
    EmpresaId     int NOT NULL,
    ClaveExclusion varchar(100) NOT NULL,      -- p. ej. 'EMP:12|ENT:asientos' -> un solo trabajo activo por clave
    Prioridad     tinyint NOT NULL DEFAULT 5,
    Payload       nvarchar(2000) NULL,          -- solo identificadores (CargaId...), nunca datos
    Estado        varchar(12) NOT NULL DEFAULT 'PENDIENTE' CHECK (Estado IN ('PENDIENTE','EN_PROCESO','OK','ERROR','VENENO')),
    Intentos      tinyint NOT NULL DEFAULT 0,
    MaxIntentos   tinyint NOT NULL DEFAULT 5,
    DisponibleEn  datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),   -- backoff exponencial
    LeaseHasta    datetime2(0) NULL,
    TraceParent   varchar(60) NULL,                                 -- continuidad de la traza OpenTelemetry
    CreadoEn      datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    Error         nvarchar(2000) NULL
);
CREATE INDEX IX_Trabajo_Cola ON etl.Trabajo (Tipo, Estado, Prioridad, DisponibleEn) INCLUDE (ClaveExclusion, EmpresaId);
CREATE UNIQUE INDEX UX_Trabajo_Exclusion ON etl.Trabajo (ClaveExclusion) WHERE Estado IN ('PENDIENTE','EN_PROCESO');
```
- **Exclusión por clave:** el índice único filtrado impide dos trabajos activos de la misma empresa y entidad. Si llega otro, se descarta o se consolida.
- **Lease:** un worker que muere libera el trabajo cuando vence el lease.
- **Reintentos:** espera exponencial (1, 2, 4, 8 y 16 min). Después de `MaxIntentos` pasa a `VENENO`, se alerta y nunca se reintenta indefinidamente.
- **Encadenamiento:** al terminar `PROCESAR_CARGA` de asientos se encola `CALCULAR_CARTERA`; al terminar las cargas del periodo, `DISTRIBUIR_COSTOS`. La primera tarea de la cadena con la foto de configuración es `AUDITAR_CONFIG`.

## 3. Concurrencia en la base de datos
| Riesgo | Mitigación |
|---|---|
| Los dashboards leen mientras el worker escribe | **READ COMMITTED SNAPSHOT** (activo por defecto en Azure SQL Database, verificar): los lectores no bloquean ni son bloqueados |
| Una carga grande bloquea tablas columnstore | Procesar por lotes en transacciones cortas (p. ej. 5.000 filas) y aplicar con `MERGE` sobre la ventana de la empresa. Nunca transacciones que abarquen toda la carga |
| 50 empresas cargando a las 23:00 | Desfase aleatorio en el conector (hasta 60 min) + **máximo N trabajos simultáneos en el worker** (p. ej. 2 en la BD gratuita) + prioridad: catálogos y asientos primero |
| Pool de conexiones con `SESSION_CONTEXT` de otro usuario | Fijar `SESSION_CONTEXT` en **cada** apertura de conexión (interceptor) y usar `@read_only = 1`. Los procesos de ETL usan su propio login (`rol_etl`) |
| Cálculos pesados (cartera, distribución de costos) | Se ejecutan en el worker después de la carga, nunca al abrir un dashboard. Los dashboards leen resultados ya calculados |
| Agotar el cupo gratuito de la BD (vCore‑segundos) | Métrica y alerta. El trabajo nocturno es el mayor consumo: medirlo en el piloto antes de sumar empresas |

## 4. Rendimiento de la API y del portal
1. **Caché de resultados de widgets:** clave `empresa + métrica + filtros + versión de carga`. Se invalida cuando termina una carga de esa empresa. En pruebas es `IMemoryCache` (gratis); con varias instancias, `HybridCache` de .NET con caché distribuida (Redis **solo con cotización previa**).
2. **Consultas asíncronas, paginadas y con `CancellationToken`:** si el usuario cambia el filtro, la consulta anterior se cancela.
3. **Rate limiting** (`Microsoft.AspNetCore.RateLimiting`): por cliente de API (ingesta), por usuario (dashboards) y por usuario y empresa (chat de IA, además del cupo).
4. **Límites de tamaño:** lote ≤ 10 MB comprimido; respuestas de widgets acotadas (top N y paginación).
5. **Chat de IA:** streaming SSE; máximo de conversaciones simultáneas por empresa; timeout por llamada; cancelación si el usuario cierra el panel.
6. **Portal Blazor:** si se usa render interactivo en servidor, cada usuario mantiene un circuito SignalR que consume memoria. Para dashboards conviene el modo interactivo **WebAssembly o Auto**, que alivia el servidor y abarata el escalado.
7. **Ingesta:** la API solo valida, guarda el lote en staging y encola. No procesa dentro de la solicitud HTTP (respuesta `202 Accepted`).

## 5. Patrones de confiabilidad
- **Idempotencia** en todo: lote (`CargaId` + número), trabajo (`ClaveExclusion`) y `MERGE` por `SourceKey`.
- **Outbox** para efectos externos (correos de alerta y resumen semanal): se escriben en `etl.Trabajo` dentro de la misma transacción del cambio que los origina y un trabajo los envía.
- **Circuit breaker y reintentos** con `Microsoft.Extensions.Http.Resilience` para llamadas salientes (proveedor de IA, correo).
- **Cancelación cooperativa** y apagado ordenado del worker: el lease protege lo que estaba en curso.

## 6. Pruebas de carga (antes de sumar empresas)
- Simular 3 empresas, luego 10 y luego 50, cada una con una carga nocturna de tamaño real (medido en el piloto), más 20 usuarios simultáneos en dashboards.
- **Medir:** duración total de la noche, vCore‑segundos consumidos, latencia p95 de widgets, errores.
- **Se hace en desarrollo local o en la BD gratuita**, nunca en recursos pagos sin aprobación (D32).
