# Spec: Generación de reportes en segundo plano (exportación e impresión)

Estado: diseño (Opus). Implementación: Sonnet. Usa la cola de `20_spec_colas_concurrencia_rendimiento.md` (D23) y respeta el costo (D32).

## 1. Regla
**Ningún reporte para imprimir o descargar (PDF o Excel) se genera dentro de la solicitud del usuario.** Se encola, se procesa en el worker con concurrencia limitada y el usuario lo descarga cuando está listo, aunque haya cerrado la sesión y vuelto después. Así se protege la BD y la experiencia del resto de usuarios.

## 2. Flujo
```
Usuario: "Generar PDF / Excel" (dashboard, informe gerencial, hallazgos, estado de cuenta IA…)
  → POST /v1/reportes            → crea rpt.SolicitudReporte (PENDIENTE) + encola GENERAR_REPORTE
  ← 202 { solicitudId }          → la UI muestra "En cola (posición N)"
Worker (máx. N simultáneos por instancia y 1 por usuario):
  → EN_PROCESO → consulta los datos con la seguridad (RLS) del usuario que pidió → genera el archivo
  → lo guarda en Blob Storage privado → LISTO (o ERROR con mensaje entendible)
  → notificación en el portal (campana) y, opcionalmente, correo con enlace al portal (no al archivo)
Usuario: "Mis reportes" → descarga mediante URL temporal (SAS de 15 min) → queda auditado en aud.Acceso
```

## 3. Modelo
```sql
CREATE TABLE rpt.SolicitudReporte (
    SolicitudId    uniqueidentifier NOT NULL CONSTRAINT PK_SolicitudReporte PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    EmpresaId      int NOT NULL CONSTRAINT FK_SolRep_Empresa REFERENCES seg.Empresa,
    UsuarioId      int NOT NULL,
    TipoReporte    varchar(60) NOT NULL,          -- DASHBOARD_PDF, INFORME_GERENCIAL, HALLAZGOS_XLSX, ESTADO_CUENTA_IA...
    Formato        varchar(5)  NOT NULL CHECK (Formato IN ('PDF','XLSX','CSV')),
    ParametrosJson nvarchar(2000) NOT NULL,       -- filtros (periodo, sede...), validados contra lista blanca
    HashParametros char(64) NOT NULL,             -- para reutilizar un resultado idéntico reciente
    Estado         varchar(12) NOT NULL DEFAULT 'PENDIENTE' CHECK (Estado IN ('PENDIENTE','EN_PROCESO','LISTO','ERROR','CANCELADO','EXPIRADO')),
    Progreso       tinyint NULL,                  -- 0-100, opcional
    BlobRuta       nvarchar(400) NULL,
    TamanoBytes    bigint NULL,
    VersionCarga   bigint NULL,                   -- datos con los que se generó
    SolicitadoEn   datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    IniciadoEn     datetime2(0) NULL,
    TerminadoEn    datetime2(0) NULL,
    ExpiraEn       datetime2(0) NULL,             -- p. ej. 7 días después de LISTO
    Error          nvarchar(500) NULL
);
CREATE INDEX IX_SolRep_Usuario ON rpt.SolicitudReporte (EmpresaId, UsuarioId, SolicitadoEn DESC);
```
(Con RLS por `EmpresaId`; cada usuario ve solo sus solicitudes y el administrador de la empresa ve las de su empresa.)

## 4. Controles de uso y de saturación
| Control | Valor inicial | Configurable |
|---|---|---|
| Reportes en curso por usuario | 1 (los demás quedan en cola) | Sí |
| Reportes en cola por usuario | 5 | Sí |
| Reportes simultáneos en el worker | 2 (BD gratuita en pruebas) | Sí |
| **Reutilización:** misma empresa, mismo reporte, mismos parámetros y misma versión de carga en las últimas 24 h | Se entrega el archivo existente sin regenerar | Sí |
| Tamaño máximo | p. ej. 200.000 filas en Excel; los PDF son resumen gerencial, no listados masivos | Sí |
| Prioridad | Estados de cuenta y hallazgos críticos antes que las exportaciones masivas | Sí |
| Horario | Exportaciones masivas fuera de la ventana de carga nocturna (23:00–02:00) | Sí |
| Caducidad | Archivos borrados a los 7 días; se puede volver a generar | Sí |

## 5. Generación
- **Datos:** los mismos que ve el usuario. Se consultan con su identidad y RLS, no con el rol del worker. Se guardan la versión de carga y los filtros en el pie del documento.
- **PDF:** plantilla de marca de la empresa (logo y colores de `seg.Empresa`), encabezado con empresa, periodo, filtros y fecha de corte, y pie con «Generado por IAnalitica · versión de datos».
- **Excel:** una hoja de datos y una de parámetros; números como números, no como texto.
- **Librerías:** se eligen al implementar, verificando su **licencia y costo** (D32). Varias librerías de PDF y Excel populares en .NET tienen licencia comercial según el tamaño de la empresa.
- **Informes programados** (el «board pack» semanal o mensual): el mismo mecanismo, disparado por una programación. Se deja en «Mis reportes» y se avisa por correo.

## 6. Interfaz
- **Botón «Generar»:** muestra el estado en línea (en cola, generando, listo) sin bloquear la pantalla.
- **«Mis reportes»:** lista con estado, fecha, parámetros, botones de descargar, cancelar si está en cola, y regenerar.
- **Notificaciones:** campana en el portal cuando un reporte queda listo o falla.

## 7. Observabilidad y auditoría
- Span de OpenTelemetry por reporte con tipo, tamaño, duración y resultado (sin datos).
- `aud.Acceso` registra cada descarga.
- Métricas: tiempo en cola, tiempo de generación y tasa de errores.
