# IAnalitica — Facturación del SaaS: cuenta de cobro, control de pagos y envío por correo

Parte de `docs/CONTEXTO.md` (índice). La numeración §N es global y se conserva entre archivos. Precedencia y reglas de uso: `01_reglas_arquitectura_seguridad.md` §0.
**Estado (rev. 3, 2026-11-05): paso 1 del plan implementado (`1040_facturacion.sql`, rama `fac` en `siempre/9000_rls.sql`, pruebas); el resto es diseño.** Decisiones nuevas del dueño: **prorrateo por días** (D48) y **el canon se cobra siempre, también en suspensión** (D51). Incorpora las decisiones vinculantes del dueño: sin DIAN, sin IVA, mes vencido, envío automático, SMTP genérico.
Convención: **[H]** = hecho verificado en el repo · **[S]** = supuesto no confirmado (§0.3) · **[NV]** = afirmación externa no verificada.
El repo usa el **mediador propio** `Application/Mediacion` (D34), no MediatR.

## 29. Alcance

**Dentro del alcance**
- Cobro mensual **por empresa (tenant)**, **mes vencido**. La cuenta de cobro del mes M se genera y se **envía automáticamente el día 1 de M+1** (America/Bogota).
- **Un concepto principal: el canon mensual.** El canon viene del plan (`ia.PlanComercial`) y puede reemplazarse por un canon pactado por empresa. El canon de plan cambia por mes completo; la condición comercial de la empresa puede empezar o terminar **a mitad de mes y se prorratea (D48)**. Moneda COP, **sin IVA**.
- **Prorrateo [S]:** por días calendario del mes, alta y baja inclusive: `ROUND(canon × días facturados / días del mes, 0)`, `AwayFromZero`. Si la condición cambia a mitad de mes, la cuenta lleva **una línea por tramo**. Parámetro `PRORRATEO = DIAS_MES`.
- **Suspensión (D51):** la empresa suspendida por mora **sigue facturándose** mientras tenga condición vigente; no hay retención ni alerta por suspensión. Solo deja de cobrarse con `VigenteHasta` (terminación).
- **Excedente de IA como segunda línea opcional**, apagada por defecto (§31). El diseño funciona solo con canon.
- Cuenta de cobro numerada en **una sola serie (CC)**, con PDF (QuestPDF) adjunto al correo. La anulación conserva el número; para corregir se emite otra CC.
- **Control de pagos:** registro de pagos, aplicación a cuentas, saldos, vencimiento, mora y edades, y conciliación manual.
- Correo por **SMTP genérico** configurable (§32.3), con outbox, reintentos, reenvío manual, bitácora y recordatorios de mora.
- Páginas Blazor para el superadmin (todo) y para el `Admin{NIT}` (solo consulta de lo suyo).

**Fuera del alcance**
- Todo lo DIAN: factura electrónica, CUFE, referencias a facturas externas.
- IVA.
- Notas crédito y débito, y ajustes tipo nota.
- Pasarela de pagos, conciliación bancaria automática e intereses de mora automáticos.
- Suspensión automática del servicio.
- Multimoneda.

**Supuestos [S]**
| # | Supuesto | Valida |
|---|---|---|
| S1 | Días de pago por defecto: **30 calendario** desde la emisión de la cuenta | Dueño + abogado |
| S2 | Montos en pesos enteros (`decimal(16,2)` con redondeo a 0 decimales, `AwayFromZero`) | Dueño |
| S3 | Destinatarios: contactos `seg.EmpresaContacto` con `Tipo = 'FACTURACION'` (para) y `'CONTADOR'` (copia). Los dos tipos ya existen en el CHECK [H] | Dueño |
| S4 | Datos del emisor (razón social, NIT, instrucciones de pago) como parámetros globales que edita el superadmin | Dueño |
| S5 | `fac.consultar` lo tiene solo `ADMIN_EMPRESA` en v1 | Dueño (P10) |

## 30. Modelo de datos (esquema `fac`)

### 30.1 Scripts
- **`database/scripts/1040_facturacion.sql`**: esquema, tablas, índices, permisos de BD, semillas. Se numera desde 1000 por la regla 0.8; el último script es 1030. El esquema se crea aquí porque `0001_esquemas.sql` ya está aplicado.
- **`1041_facturacion_procedimientos.sql`**: procedimientos (`CREATE OR ALTER`).
- **`1042_ia_periodo_colombia.sql`**: periodo de IA en hora Colombia (§31.2). Es obligatorio antes de activar la línea de excedente.
- **Único cambio a un script existente: `siempre/9000_rls.sql`.** Es RunAlways y se regenera en cada despliegue. Hay que agregar `N'fac'` a la lista de esquemas y una rama propia (§30.4). Hoy `fac` no está en el `WHERE s.name IN (...)` [H]: **sin este cambio las tablas `fac` quedan sin RLS.**
- Los permisos de BD van en 1040, porque `0900` concede por esquema y no cubre uno nuevo [H].

### 30.2 Numeración
- **Una sola serie global `CC`.** Se asigna en el momento de emitir, no al crear el borrador, así que los borradores descartados no consumen números.
- **Global y no por empresa:** hay un solo emisor, el número es único para conciliar el banco y evita tener «CC-0001» repetido en cada cliente.
- **Concurrencia:** una fila en `fac.Consecutivo`, con `UPDATE … SET @n = Ultimo = Ultimo + 1` dentro de la transacción que emite. El bloqueo de fila serializa a los emisores y el rollback devuelve el número: **no quedan huecos por fallos**. No se usa `SEQUENCE` porque no garantiza ausencia de huecos.
- **Anulación:** la cuenta pasa a `ANULADA`, conserva su número y su PDF. La corrección es una CC nueva, con número nuevo y `ReemplazaA`. Un número nunca se reutiliza.

### 30.3 DDL
**Implementado:** `database/scripts/1040_facturacion.sql` es la fuente única (regla: manda `database/scripts`). No se duplica aquí. Resumen:
- **Parámetros y catálogos globales** (sin `EmpresaId`, escritura solo vía procedimientos): `fac.Parametro`, `fac.CanonPlan` (vigencia por mes completo), `fac.Consecutivo` (serie única `CC`), `fac.TareaProgramada`.
- **Por empresa (con RLS):** `fac.CondicionComercial` (vigencias por día, una sola abierta), `fac.CuentaCobro`, `fac.CuentaCobroLinea`, `fac.LiquidacionIa`, `fac.DetalleIa`, `fac.Pago`, `fac.AplicacionPago`, `fac.Envio`, `fac.EnvioDestinatario`, `fac.Alerta` (solo superadmin).
- **Prorrateo:** cada línea `CANON` guarda `CondicionId`, `FechaDesde`/`FechaHasta`, `DiasFacturados`, `DiasPeriodo` y `CanonMensualCop`; el CHECK exige `DiasFacturados = días del tramo` y `≤ DiasPeriodo`. Única por `(CuentaCobroId, Tipo, FechaDesde)`.
- **Integridad:** FK compuestas `(Id, EmpresaId)` impiden aplicar el pago de una empresa a la cuenta de otra; el número solo existe en `EMITIDA`/`PAGADA`/`ANULADA`; una cuenta viva por empresa y periodo; el número es único.
- **Permisos de BD:** `rol_app` lee todo `fac` y escribe en las tablas operativas (el BLOCK de RLS deja escribir solo al superadmin); `DENY` de escritura sobre las tablas globales y de `DELETE` sobre cuentas y pagos; `rol_etl` con todo. Permisos de aplicación: `fac.administrar` (SUPERADMIN) y `fac.consultar` (SUPERADMIN, ADMIN_EMPRESA).
- **No incluido en este paso:** el índice de línea de tiempo sobre `aud.CambioAdministrativo` (la tabla está protegida por la política con `SCHEMABINDING`; se evaluará con el paso 2 contra una BD real).

**Procedimientos (`1041`).** Son de `dbo`, igual que las tablas, así que el encadenamiento de propiedad salta los `DENY`. Por eso **cada uno valida el contexto él mismo**:
`IF IS_MEMBER(N'rol_etl') = 0 AND ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;`
- `fac.usp_SiguienteNumero @Numero OUTPUT`: exige `@@TRANCOUNT > 0` y devuelve `'CC-' + RIGHT('000000' + CAST(@n AS varchar(10)), 6)`.
- `fac.usp_EmitirCuentaCobro @CuentaCobroId, @VersionFila NULL, @UsuarioId NULL`: con `UPDLOCK`, valida `BORRADOR` (y `VersionFila` si viene). Asigna el número, toma la foto del cliente desde `seg.Empresa` y fija `FechaEmision` = hoy en Bogotá y `FechaVencimiento = FechaEmision + DiasPago`. Pasa a `EMITIDA`, marca `ia.CorteMensual.Estado = 'FACTURADO'` si hay línea IA y encola `GENERAR_PDF_CUENTA_COBRO` (outbox). Todo en una transacción.
- `fac.usp_AnularCuentaCobro @Id, @Motivo, @UsuarioId`: exige `PagadoCop = 0`; si quedan aplicaciones, primero se reversan.
- `fac.usp_AplicarPago`: `UPDLOCK` sobre el pago y después sobre la cuenta, siempre en ese orden. Si el saldo queda en 0, la cuenta pasa a `PAGADA`.
- `fac.usp_ReversarAplicacion`: si la cuenta estaba `PAGADA`, vuelve a `EMITIDA`.
- `fac.usp_GuardarCanonPlan`, `fac.usp_GuardarParametro`, `fac.usp_GuardarCondicion`. Esta última cierra la condición abierta y abre la nueva sin solapamiento.

### 30.4 RLS (cambio en `siempre/9000_rls.sql`)
Agregar `N'fac'` a `s.name IN (...)` y estas ramas antes del `ELSE`:
```sql
WHEN Nombre = N'fac.Alerta' THEN
    N'ADD FILTER PREDICATE seg.fn_SoloSuperadmin(EmpresaId) ON ' + Tabla
  + N', ADD BLOCK PREDICATE seg.fn_SoloSuperadmin(EmpresaId) ON ' + Tabla + N' AFTER INSERT'
  + N', ADD BLOCK PREDICATE seg.fn_SoloSuperadmin(EmpresaId) ON ' + Tabla + N' AFTER UPDATE'
WHEN Nombre LIKE N'fac.%' THEN
    N'ADD FILTER PREDICATE seg.fn_FiltroEmpresa(EmpresaId) ON ' + Tabla
  + N', ADD BLOCK PREDICATE seg.fn_SoloSuperadmin(EmpresaId) ON ' + Tabla + N' AFTER INSERT'
  + N', ADD BLOCK PREDICATE seg.fn_SoloSuperadmin(EmpresaId) ON ' + Tabla + N' AFTER UPDATE'
  + N', ADD BLOCK PREDICATE seg.fn_SoloSuperadmin(EmpresaId) ON ' + Tabla + N' BEFORE UPDATE'
  + N', ADD BLOCK PREDICATE seg.fn_SoloSuperadmin(EmpresaId) ON ' + Tabla + N' BEFORE DELETE'
```
- El `Admin{NIT}` **solo lee** lo de su empresa y la BD le impide escribir en `fac`. Escriben el superadmin y el worker con `rol_etl` (`fn_SoloSuperadmin` ya existe [H]).
- La RLS es por fila, no por columna: todo campo de una fila con `EmpresaId` es visible para ese `Admin{NIT}`. Por eso no se guarda texto interno en `fac.*` (salvo en `fac.Alerta`, que solo ve el superadmin).
- Las tablas sin `EmpresaId` son legibles por `rol_app`. Ocultar `CanonPlan` al cliente queda a cargo de la aplicación, igual que hoy con `ia.PlanComercial` [H].

### 30.5 Estados
```
(generación) ─sin motivos de retención y sin aprobación requerida─► EMITIDA ──saldo 0──► PAGADA
     └─con motivos o con aprobación requerida─► BORRADOR ─emitir (superadmin)─► EMITIDA      ▲  │ reverso
                                                  ├─regenerar (recalcula; sigue BORRADOR)    └──┘
                                                  └─descartar(motivo)─► DESCARTADA (sin número)
EMITIDA (PagadoCop = 0) ─anular(motivo)─► ANULADA (conserva número) ─► nueva CC con ReemplazaA + correo ANULACION
```
- «Vencida» y «en mora» **no son estados**: se derivan de `SaldoCop > 0 AND FechaVencimiento < hoy Bogotá`.
- El PDF y el envío tienen su propio ciclo en `PdfRuta` y `fac.Envio`. Una cuenta `EMITIDA` puede tener un envío `FALLIDO`; eso genera alerta y el superadmin la reenvía.

### 30.6 Quién ve qué
| Dato | Superadmin (`fac.administrar`) | `Admin{NIT}` (`fac.consultar`) |
|---|---|---|
| Canon por plan, parámetros, SMTP (solo estado) | Ve y edita (el SMTP solo se ve, §32.3) | No |
| Condición comercial | Ve y edita | Ve la suya (plan, canon, días de pago) |
| Cuentas, líneas, detalle IA | Todas, con todas las acciones | Las suyas `EMITIDA`/`PAGADA`/`ANULADA`; **los borradores no** (lo filtra el handler); PDF |
| Pagos y aplicaciones | Todo, incluido el soporte | Los suyos, sin `SoporteRuta` |
| Envíos | Todo, con reintento y reenvío | Fecha, tipo y estado de los suyos |
| Alertas | Todas | Nunca (RLS) |
| Costo del proveedor de IA (USD) y margen | Sí, desde `ia.CorteMensual` | Nunca |
| `aud.CambioAdministrativo` de `fac.*` | Todo | Lo de su empresa (`fn_FiltroAuditoria` [H]): el `Motivo` de anulaciones y reversos es visible al cliente |

- Toda escritura queda en `aud.CambioAdministrativo`: `EMITIR`, `ANULAR`, `REGISTRAR_PAGO`, `APLICAR`, `REVERSAR`, `CAMBIAR_CANON`, `CAMBIAR_PARAMETRO`, `REENVIAR`…
- Cada descarga de PDF queda en `aud.Acceso` con `DESCARGA_CUENTA_COBRO`.

## 31. Excedente de IA (línea opcional) y periodo en hora Colombia

### 31.1 Recomendación
**Mantener el excedente como segunda línea de la misma cuenta, detrás de `INCLUIR_EXCEDENTE_IA` (por defecto 0).**
- Por qué se mantiene: el contrato lo prevé (SEXTA (b), Anexo C §3) y los datos ya existen (`ia.CorteMensual`, `ia.UsoLlamada`) [H].
- Por qué arranca apagada: hoy la IA es solo interna (fase 1, §7 [H]), el plan `PILOTO` tiene cupo 0 y no hay tarifa publicada para el cliente.
- Se enciende cuando la IA llegue a clientes (fase 2), con `1042` aplicado. **Decisión abierta D50.** Apagada, la cuenta tiene solo la línea `CANON`.

### 31.2 Periodo en hora Colombia (resuelto en el diseño)
- **Hallazgo [H]:** `ia.usp_ReservarCuota`, `ia.usp_LiquidarReserva` y `RepositorioAsistente` calculan `PeriodoKey` (y el día del tope por usuario) con `SYSUTCDATETIME()`. Por eso el mes cambia a las **19:00 Bogotá** del último día, en contra de §2.
- **Solución (`1042_ia_periodo_colombia.sql`, `CREATE OR ALTER`):** calcular `@Periodo` y el día con `DATEADD(HOUR, -5, SYSUTCDATETIME())`. Colombia está en UTC−5 sin horario de verano, igual a `America/Bogota`. Las marcas técnicas siguen en UTC. Hay que ajustar también la consulta de `RepositorioAsistente` (línea 56 [H]).
- **Riesgo de transición:** el mes en que se despliega tiene un borde mixto. Hay que desplegar a mitad de mes y antes de encender `INCLUIR_EXCEDENTE_IA`.
- **Ventana del detalle:** `ia.UsoLlamada.Fecha` está en UTC, así que el mes M de Bogotá es `[M-01 05:00 UTC, (M+1)-01 05:00 UTC)`.
- **El canon no depende de nada de esto:** es un valor mensual.

### 31.3 Fórmula (función pura en Domain)
```
Bruto      = ia.CorteMensual.ExcedenteCop                       -- max(0, consumo − cupo congelado al abrir el mes) [H]
Autorizado = EXISTS(ia.UsoLlamada del mes con EnExcedente = 1)  -- solo existe con una reserva OK_EXCEDENTE [H]
Facturable = Autorizado ? min(Bruto, ISNULL(EmpresaConfig.TopeExcedenteCop, Bruto)) : 0
Absorbido  = Bruto − Facturable                                  -- no se cobra; queda para el margen
Retener    = CambioConfigEnPeriodo (aud: ia.EmpresaConfig en el mes) OR |Σ UsoLlamada.ValorVentaCop − Corte.ValorConsumoCop| > UMBRAL_CUADRE_IA_COP
```
- **Valoración:** no se revaloriza. Cada llamada ya trae su `ValorVentaCop` con la `TarifaId` vigente en ese momento [H]. Si la tarifa cambia a mitad de mes, el detalle muestra dos filas.
- **Regla nueva:** prohibido crear una `ia.Tarifa` con `VigenteDesde` anterior a hoy.
- **Cierre:** `CERRAR_PERIODO_IA` corre a las 01:00 Bogotá del día 1, después del vencimiento de las reservas de M (10 min [H]). `usp_CerrarPeriodo` es idempotente [H] y hoy nadie lo llama.
- **Empresa sin uso de IA:** no tiene corte, así que el excedente es 0 y la cuenta no lleva línea IA.
- **Consumo tardío:** el corte es definitivo. Lo que se liquide después del corte se registra como diferencia y no se cobra; no hay ajustes en cuentas posteriores. Tras el margen de 01:00 es prácticamente cero: una reserva de M vencida se liquida en el periodo actual [H].
- **Cupo vivo vs. congelado [H]:** la reserva usa el cupo actual y el corte usa el congelado. Por eso los cambios de plan o cupo rigen el día 1 siguiente. Si se necesita un cambio inmediato, el comando actualiza también `ConsumoPeriodo.CupoIncluidoCop`.

## 32. Capas y contratos

### 32.1 Domain — `src/IAnalitica.Domain/Facturacion/`
```csharp
public static class EstadosCuentaCobro { public const string Borrador = "BORRADOR", Descartada = "DESCARTADA", Emitida = "EMITIDA", Pagada = "PAGADA", Anulada = "ANULADA";
    public static bool PuedePasar(string de, string a); }
public static class Canon { public static decimal? DelPeriodo(decimal? pactadoCop, decimal? canonPlanCop); }   // pactado gana; null → SIN_CANON
public sealed record EntradaReglasEmision(bool EmpresaActiva, decimal? Canon, int Destinatarios, decimal Total, decimal? TotalMesAnterior,
    decimal VariacionMaxPct, bool RequiereAprobacion, IReadOnlyList<string> AdvertenciasIa);
public static class ReglasEmision { public static IReadOnlyList<string> MotivosRetencion(EntradaReglasEmision e); }   // vacío = emitir
public sealed record EntradaExcedenteIa(decimal ConsumoCop, decimal CupoCop, decimal BrutoCop, bool HuboConsumoEnExcedente, decimal? TopeCop,
    bool CambioConfigEnPeriodo, decimal SumaUsoLlamadasCop, decimal UmbralCuadreCop);
public sealed record ResultadoExcedenteIa(decimal FacturableCop, decimal AbsorbidoCop, decimal? TopeAplicadoCop, decimal DiferenciaCuadreCop, IReadOnlyList<string> Advertencias);
public static class LiquidadorExcedenteIa { public static ResultadoExcedenteIa Calcular(EntradaExcedenteIa e); }
public static class Vencimiento { public static DateOnly Calcular(DateOnly emision, short dias) => emision.AddDays(dias); }
public static class EdadCartera { public static string Rango(DateOnly vence, DateOnly hoy); }   // AL_DIA, 1_30, 31_60, 61_90, MAS_90
```

### 32.2 Application — `src/IAnalitica.Application/Facturacion/` y `Abstracciones/Correo.cs`
```csharp
// Abstracciones/Correo.cs — genérico: lo reutilizan después el resumen del auditor y las alertas (pendiente 6 de 06)
public sealed record DireccionCorreo(string Email, string? Nombre);
public sealed record AdjuntoCorreo(string NombreArchivo, string TipoMime, ReadOnlyMemory<byte> Contenido);
public sealed record CorreoSaliente(string MessageId, IReadOnlyList<DireccionCorreo> Para, IReadOnlyList<DireccionCorreo> Copia,
    string Asunto, string CuerpoTexto, string? CuerpoHtml, IReadOnlyList<AdjuntoCorreo> Adjuntos);
public enum ResultadoCorreo { Aceptado, FallaTransitoria, FallaPermanente, NoConfigurado }
public sealed record ResultadoEnvioCorreo(ResultadoCorreo Resultado, string? CodigoSmtp, string? CodigoError);   // jamás texto del servidor
public interface ICorreoSaliente
{
    Task<ResultadoEnvioCorreo> EnviarAsync(CorreoSaliente correo, CancellationToken ct);
    Task<ResultadoEnvioCorreo> ProbarConexionAsync(CancellationToken ct);   // botón «probar» del superadmin
}

// Facturacion/Contratos.cs
public sealed record DocumentoCuentaCobro(EmisorDoc Emisor, ClienteDoc Cliente, string Numero, DateOnly Emision, DateOnly Vence, int PeriodoKey,
    IReadOnlyList<LineaDoc> Lineas, IReadOnlyList<DetalleIaDoc> DetalleIa, decimal Total, decimal SaldoAnteriorPendiente, string InstruccionesPago, bool Anulada);
public interface IGeneradorPdfCuentaCobro { byte[] Generar(DocumentoCuentaCobro doc); }      // puro, en memoria
public interface IAlmacenDocumentosFacturacion                                             // separado de IAlmacenReportes: sin caducidad de 7 días
{ Task<string> GuardarAsync(int empresaId, string nombre, byte[] contenido, CancellationToken ct); Task<Stream?> AbrirAsync(string ruta, CancellationToken ct); }
public interface IRepositorioFacturacion   // Dapper con rol_app + contexto de sesión (RLS); escrituras sensibles por los procedimientos de §30.3
{
    Task<IReadOnlyList<CuentaCobroResumen>> ListarAsync(FiltroCuentas filtro, CancellationToken ct);
    Task<CuentaCobroDetalle?> ObtenerAsync(int cuentaCobroId, CancellationToken ct);
    Task<Result<string>> EmitirAsync(int cuentaCobroId, byte[] versionFila, int usuarioId, CancellationToken ct);
    Task<Result<int>> AnularYReemplazarAsync(int cuentaCobroId, string motivo, bool reemplazar, int usuarioId, CancellationToken ct);
    Task<int> RegistrarPagoAsync(int empresaId, NuevoPago pago, int usuarioId, CancellationToken ct);
    Task<Result> AplicarPagoAsync(int pagoId, int cuentaCobroId, decimal valorCop, int usuarioId, CancellationToken ct);
    Task<Guid> EncolarEnvioAsync(int cuentaCobroId, string tipo, string? hito, bool esReenvio, int? usuarioId, CancellationToken ct);  // Envio + destinatarios + etl.Trabajo, una transacción
    // … ReversarAplicacionAsync, AnularPagoAsync, ListarCarteraAsync, EstadoCuentaEmpresaAsync, ListarAlertasAsync, AtenderAlertaAsync
}
```
- **Comandos.** Todos llevan `[RequierePermiso("fac.administrar")]` e `IComandoTransaccional`, y el handler verifica `sesion.EsSuperAdmin`.
- **Excepción acotada a «prohibido recibir `EmpresaId` del cliente»:** el superadmin actúa sobre una empresa elegida. Se valida contra `seg.Empresa` y el BLOCK de RLS frena a cualquier otro rol.
- **Lista de comandos:**
  - `GuardarCanonPlanCommand(short PlanId, DateOnly VigenteDesde, decimal CanonMensualCop)`
  - `GuardarCondicionComercialCommand(int EmpresaId, DateOnly VigenteDesde, short PlanId, decimal? CanonPactadoCop, short? DiasPago, bool? RequiereAprobacion, bool Facturable, string? ReferenciaContrato)`
  - `GuardarParametroFacturacionCommand(string Clave, string Valor)`
  - `GenerarCuentasPeriodoCommand(int PeriodoKey, int? EmpresaId)` (encola; para rehacer manualmente)
  - `RegenerarBorradorCommand(int Id)`
  - `EmitirCuentaCobroCommand(int Id, byte[] VersionFila)` (libera un borrador retenido)
  - `DescartarBorradorCommand(int Id, string Motivo)`
  - `AnularCuentaCobroCommand(int Id, string Motivo, bool EmitirReemplazo)`
  - `ReintentarEnvioCommand(Guid EnvioId)` (vuelve a encolar un `FALLIDO` con los mismos destinatarios)
  - `ReenviarCuentaCobroCommand(int Id)` (envío nuevo con los destinatarios actuales)
  - `MarcarEnvioRebotadoCommand(Guid EnvioId, string CodigoError)`
  - `RegistrarPagoCommand(int EmpresaId, string Clase, string? Medio, DateOnly FechaPago, decimal ValorCop, string? Referencia, bool AplicarAutomatico)`
  - `AplicarPagoCommand(int PagoId, IReadOnlyList<AplicacionSolicitada> Aplicaciones)`
  - `ReversarAplicacionCommand(int AplicacionId, string Motivo)`
  - `AnularPagoCommand(int PagoId, string Motivo)`
  - `AtenderAlertaCommand(int AlertaId)`
  - `EnviarCorreoPruebaCommand()`
- **Validadores:** montos > 0; el periodo ya cerrado; `Motivo` de 10 a 500 caracteres; `Referencia` sin secuencias de ≥ 10 dígitos.
- **Consultas:**
  - `ListarCuentasCobroQuery(int? EmpresaId, int? PeriodoKey, string? Estado, bool SoloMora)` (`fac.consultar`; para quien no es superadmin, la empresa sale de la sesión y se excluyen los borradores)
  - `ObtenerCuentaCobroQuery(int Id)`
  - `EstadoCuentaEmpresaQuery()`
  - `CarteraFacturacionQuery(DateOnly Corte)` (superadmin)
  - `ListarPagosQuery`
  - `ListarEnviosQuery(bool SoloFallidos)`
  - `ListarAlertasQuery`
  - `EstadoCorreoQuery` (configurado sí/no, servidor, puerto, modo, remitente; **nunca la clave**)

### 32.3 Infrastructure — `src/IAnalitica.Infrastructure/Facturacion/` y `Correo/`
- `RepositorioFacturacion.cs`: Dapper sobre `IFabricaConexiones` (`rol_app`), con el patrón de `RepositorioReportes` [H].
- `GeneradorCuentas.cs` (`rol_etl`):
  - Toma `sp_getapplock 'fac:{empresa}:{periodo}'`. Si ya hay una cuenta viva para ese periodo, termina sin hacer nada.
  - Calcula el canon y, si está activa, la línea IA. Aplica `ReglasEmision`.
  - Inserta la cuenta y sus líneas. Sin motivos de retención, llama a `usp_EmitirCuentaCobro` en la misma transacción. Con motivos, la deja en `BORRADOR` y crea las `fac.Alerta`.
  - `UX_FacCC_Periodo` es la última barrera contra duplicados.
- `PdfCuentaCobro.cs`: QuestPDF 2026.8.0 con `LicenseType.Community`, igual que `ExportadorPdf` [H]. Solo recibe el DTO, así que no puede incluir nada que el DTO no traiga.
- `AlmacenDocumentosDisco.cs`: raíz propia (`Facturacion:Almacen:Ruta`), sin expiración, con el control de ruta de `AlmacenReportesDisco` [H].
- **`Correo/CorreoSmtp.cs` (`ICorreoSaliente`) con MailKit.** NuGet muestra la 4.18.2 (MIT, `net10.0`, 2026-10-23) [H, consultado 2026-11-05]; la versión se confirma al instalar. Usa `SmtpClient.ConnectAsync(servidor, puerto, SecureSocketOptions, ct)`, `AuthenticateAsync`, `SendAsync(MimeMessage)` y `BodyBuilder.Attachments.Add`.
  - Clasificación de fallas: `SmtpCommandException` 5xx → permanente, 4xx → transitoria; `AuthenticationException` → permanente + alerta `SMTP_NO_CONFIGURADO`; socket o timeout → transitoria.
  - **Prohibido usar `ProtocolLogger` y registrar `ex.Message`**, porque traen direcciones. Solo se registran el tipo, el código y `EnvioId`.
  - `System.Net.Mail.SmtpClient` se descarta porque Microsoft lo desaconseja para desarrollo nuevo.
- **`OpcionesCorreoSmtp`, sección `Correo:Smtp`:**
  | Opción | Tipo | Nota |
  |---|---|---|
  | `Servidor` | string | |
  | `Puerto` | int | |
  | `Seguridad` | enum `None` \| `StartTls` \| `SslOnConnect` | Se traduce 1:1 a `SecureSocketOptions` de MailKit. `Auto` y `StartTlsWhenAvailable` **no se exponen**: el modo es siempre explícito. `None` solo en `Development` (validación al arrancar) |
  | `Usuario` | string | |
  | `Clave` | secreto | **Nunca** en `appsettings`, repo, BD ni logs |
  | `RemitenteEmail`, `RemitenteNombre` | string | |
  | `ResponderA` | string? | Buzón real del prestador |
  | `TimeoutSegundos` | int (30) | |
  | `Modo` | `Deshabilitado` \| `Archivo` \| `Smtp` | `Archivo` escribe .eml en disco para desarrollo y pruebas |

  Se valida con `ValidateOnStart`. Si falta la configuración, el worker arranca igual: los envíos quedan `FALLIDO`/`SMTP_NO_CONFIGURADO` y se crea una alerta global.
- **Dónde viven los parámetros.** Recomendación: **solo en la configuración de despliegue**. Los valores no secretos van en `appsettings.{Ambiente}.json` o en variables de entorno de Container Apps. La `Clave` va en Key Vault y se lee como secreto (`Correo__Smtp__Clave`); en local, User Secrets. **No se edita desde la UI cifrada en BD**, por tres razones:
  1. §3 prohíbe secretos en la BD [H].
  2. Una clave en la BD, aunque esté cifrada, exige gestionar llaves de cifrado (Data Protection, que hoy no persiste en Key Vault: pendiente 10 [H]) y amplía la superficie ante una inyección SQL o un respaldo filtrado.
  3. Cambiar el SMTP es raro y conviene que pase por el despliegue controlado.

  La UI del superadmin muestra el estado en solo lectura y tiene «probar conexión» y «enviar correo de prueba». **Hoy no hay carga de Key Vault en el código** [H, grep]: entra con F6.
- **Recomendación operativa de SPF/DKIM/DMARC:** remitente en un dominio propio (de preferencia un subdominio dedicado), con SPF y DKIM publicados según el proveedor SMTP y DMARC alineado. DMARC empieza en `p=none` y pasa a `quarantine` tras unas semanas limpias. Sin esto los correos caen en spam y se facilita la suplantación. Depende de P8 (dominio).
- `DependencyInjection.cs`: métodos nuevos `AddFacturacion()`, `AddCorreoSaliente(IConfiguration)` y `AddProcesamientoFacturacion()`. Los existentes no se tocan.

### 32.4 Worker
Hoy el worker solo consume la cola y no tiene programador [H]. Se agrega `src/IAnalitica.Worker/ProgramadorFacturacion.cs`, un `BackgroundService` que cada 5 min lee la hora de Bogotá con `TimeProvider` y, para cada tarea vencida cuya `Clave` no esté en `fac.TareaProgramada`, encola el trabajo. La idempotencia real está en `fac.TareaProgramada` y en los índices únicos, porque `UX_Trabajo_Exclusion` solo excluye mientras el trabajo está activo [H].

**Horario (America/Bogota)**
| Cuándo | Tipo | ClaveExclusion | Conexión | Qué hace |
|---|---|---|---|---|
| Día `DIA_PREVALIDACION` (25), 08:00 | `PREVALIDAR_FACTURACION` | `FAC:PRE\|PER:{M}` | `rol_etl` | Simula la generación de M **sin escribir cuentas** y crea alertas (`SIN_CANON`, `SIN_DESTINATARIO`, `SIN_CONDICION`) para corregir antes del día 1 |
| Día 1, 01:00 | `CERRAR_PERIODO_IA` | `IA:CIERRE\|PER:{M}` | `rol_etl` | `EXEC ia.usp_CerrarPeriodo` |
| Día 1, 06:00 | `GENERAR_CUENTAS_COBRO` | `FAC:GEN\|PER:{M}` | `rol_etl` | Con línea IA activa, exige el cierre hecho (si no, posterga). Encola `GENERAR_CUENTA` por cada empresa con condición `Facturable` vigente en M |
| inmediato | `GENERAR_CUENTA` | `FAC:CC\|EMP:{e}\|PER:{M}` | `rol_etl` | `GeneradorCuentas`: emite o retiene |
| inmediato | `GENERAR_PDF_CUENTA_COBRO` | `FAC:PDF\|CC:{id}` | `rol_etl` | Genera el PDF desde la foto, lo guarda, calcula el hash y en la misma transacción crea `fac.Envio` + destinatarios + `ENVIAR_CORREO` |
| inmediato | `ENVIAR_CORREO` | `CORREO:{EnvioId}` | `rol_etl` | Si ya está `ENVIADO`, completa. Si no, `ENVIANDO`, `Intentos + 1` y envía. Falla transitoria → reintento de la cola (1/2/4/8/16 min [H]); permanente o último intento → `FALLIDO` + alerta |
| Día 1, 09:00 y diario 07:30 | `RESUMEN_ALERTAS` | `FAC:RES\|DIA:{d}` | `rol_etl` | Si hay alertas abiertas, correo a `CORREO_OPERACION` con conteos por código y enlace a `/admin/facturacion`. **Sin nombres, correos ni montos** |
| Diario 08:00 | `REVISAR_MORA` | `FAC:MORA\|DIA:{d}` | `rol_etl` | Hitos `PREVIO_n` / `MORA_n` idempotentes por `UX_FacEnvio_Hito`, con enlace y **sin adjunto**, más la campana del administrador (`seg.Notificacion`, `UsuarioId NULL` [H]) |

- **Por qué a las 06:00:** queda fuera de la ventana nocturna de cargas (23:00–02:00, §26 [H]) y después del cierre de IA, y el correo llega al inicio del día.
- El vencimiento corre en días calendario, así que no importa si el día 1 es festivo.

### 32.5 Web (Blazor SSR)
- Políticas nuevas en `Program.cs`: `fac.administrar` y `fac.consultar`, con `Reclamos.Permiso` [H].
- **Superadmin** (`Components/Pages/Admin/Facturacion/`):
  - `/admin/facturacion`: alertas abiertas, retenidas, envíos fallidos, cartera por edades. Interruptor global «requiere aprobación».
  - `/admin/facturacion/cuentas/{id}`: emitir, descartar, regenerar, anular con reemplazo, reintentar envío, reenviar, línea de tiempo desde `aud`.
  - `/admin/facturacion/pagos`.
  - `/admin/facturacion/condiciones/{empresaId}`, con el interruptor por empresa.
  - `/admin/facturacion/planes`.
  - `/admin/facturacion/correo`: estado en solo lectura, probar conexión, correo de prueba.
- **`Admin{NIT}`:**
  - `/facturacion`: saldo, vencido, cuentas con estado derivado.
  - `/facturacion/{id}`.
  - `/facturacion/contactos`: contactos `FACTURACION`/`CONTADOR` de `seg.EmpresaContacto`, auditados.
- Descarga en `Consultas/EndpointsFacturacion.cs`: `GET /facturacion/{id}/pdf`. Exige sesión y `fac.consultar`, lee con RLS, registra en `aud.Acceso` y responde con `Cache-Control: no-store`.

## 33. Flujo, reglas de omisión y riesgos del envío automático

1. **Día 25 de M:** la prevalidación genera alertas, para corregir antes del cierre.
2. **Día 1 de M+1, 06:00:** se generan las cuentas. Si no hay motivos de retención, se **emiten y envían solas**; si hay motivos, quedan en `BORRADOR` con alerta.
3. **Correo de la cuenta:** PDF adjunto a `FACTURACION` (para) y `CONTADOR` (copia), con máximo 5 destinatarios. Asunto: `Cuenta de cobro CC-000123 · <RazónSocial> · oct-2026`. El cuerpo lleva número, total, fecha de vencimiento, enlace a `/facturacion/{id}` e instrucciones de pago. Texto plano + HTML simple, sin imágenes remotas.
4. **Fallos:** los reintentos los maneja la cola. La entrega es **al menos una vez**: si el proceso muere entre la aceptación del SMTP y la marca `ENVIADO`, el correo se duplica. Se acepta y se usa el mismo `Message-ID`.
   - **Rebotes asíncronos:** no se capturan en v1. El superadmin marca `REBOTADO` y reenvía.
   - **Reintentar** = mismo envío. **Reenviar** = envío nuevo con los destinatarios actuales.
5. **Pagos:** registro manual de recaudos y retenciones. Con «aplicar automático» se aplica FIFO por vencimiento; lo que sobra queda como saldo a favor. Con saldo 0, la cuenta pasa a `PAGADA`.
6. **Mora:** corre desde el día siguiente a `FechaVencimiento` de la cuenta de cobro. Se envían recordatorios y avisos. La suspensión es manual.
7. **Corrección:** anular (antes se reversan las aplicaciones) → nueva CC con `ReemplazaA` → correo `ANULACION` sin adjunto («la CC-000123 fue anulada y reemplazada por la CC-000130»), seguido del envío de la nueva.

**Reglas de omisión o retención (sin envío; alerta al superadmin)**
| Condición | Acción | Alerta |
|---|---|---|
| Empresa activa sin `CondicionComercial` vigente | No genera | `SIN_CONDICION` |
| `Facturable = 0` | No genera | — |
| Canon indefinido (sin pactado ni `CanonPlan` vigente) | `BORRADOR` | `SIN_CANON` |
| Total = 0 | No genera | `TOTAL_CERO` (informativa) |
| `seg.Empresa.Activa = 0` (suspendida por mora) | **Se factura igual (D51)**: la suspensión no detiene el cobro | — |
| Sin contacto `FACTURACION` | `BORRADOR` sin número. **No se emite una cuenta que el cliente no recibiría**, porque el vencimiento correría sin aviso | `SIN_DESTINATARIO` |
| Total con variación > `VARIACION_MAX_PCT` frente al mes anterior | `BORRADOR` | `VARIACION_ANOMALA` |
| Línea IA con cuadre fallido o cambio de configuración en el mes | `BORRADOR` | `CUADRE_IA` / `CAMBIO_CONFIG_IA` |
| `RequiereAprobacion` (global o por empresa) = 1 | `BORRADOR` | `APROBACION_REQUERIDA` |
| SMTP no configurado o con autenticación fallida | Se emite y el envío queda `FALLIDO` | `SMTP_NO_CONFIGURADO` (global) |
| Trabajo `VENENO` de facturación | — | `TRABAJO_VENENO` |

**Riesgos de enviar sin revisión y cómo se controlan**
- **Monto errado:** se controla con la variación anómala, la prevalidación del día 25 y el canon con vigencia mensual. La corrección es anular + reemitir, con trazabilidad.
- **Cobro a una empresa suspendida o terminada:** la suspendida se cobra siempre (D51); la terminada deja de cobrarse con `VigenteHasta`, y el último mes se prorratea.
- **Destinatario equivocado o desactualizado:** expone información comercial (cláusula NOVENA). Solo se usan contactos registrados; el administrador los mantiene y quedan auditados.
- **Primer ciclo real:** el valor por defecto es «sin aprobación» (decisión del dueño). Recomendación operativa: encender el interruptor global para el primer ciclo y apagarlo después.

**Ajuste a la regla de §26 (texto propuesto para `05`, no editado):** «Los reportes se notifican con enlace al portal, nunca con el archivo. **Excepción (D42): la cuenta de cobro se envía como PDF adjunto**, solo a los contactos de facturación registrados por la empresa. El PDF solo contiene datos comerciales (emisor, cliente, número, fechas, líneas, consumo de IA agregado por modelo) y nunca datos de usuarios, conversaciones ni pacientes. Los recordatorios, los avisos de mora y las anulaciones van sin adjunto.»

## 34. Seguridad, legal y riesgos
- **PII en logs y trazas:** nunca van direcciones, nombres, asunto, cuerpo, adjunto, NIT ni razón social. Atributos permitidos: `empresa.id`, `fac.cuenta_id`, `fac.envio_id`, `smtp.codigo`, `fac.resultado`. Hace falta una prueba específica: una excepción de MailKit que contiene una dirección no debe llegar al exportador. En la BD, las direcciones viven solo en `seg.EmpresaContacto` y en `fac.EnvioDestinatario`.
- **Retención de destinatarios:** propuesta de 2 años y luego anonimizar (lo confirma el abogado). Los datos de contacto comerciales son del prestador como responsable (política 02 [H]).
- **Fraude por correo (BEC):** los datos bancarios solo viven en `fac.Parametro`, cambian solo por el superadmin y quedan en `aud`. El correo lleva la leyenda fija «nunca cambiamos datos bancarios por correo». SPF, DKIM y DMARC deben estar listos antes del primer envío.
- **Naturaleza del documento:** el dueño decidió cuenta de cobro sin DIAN ni IVA. **La procedencia tributaria es responsabilidad del dueño y su contador** y queda fuera de este diseño; se deja constancia como riesgo, no como requisito.
- **Contrato.** Hoy dice «[N] días desde la factura electrónica» (SEXTA (a)) [H], lo que no cuadra con este flujo. **Texto sugerido para el abogado** (no se editó el contrato):
  - SEXTA (a): «El canon mensual se cobra **mes vencido**. EL PRESTADOR remitirá el primer día de cada mes, al correo de facturación que EL CLIENTE registre en la plataforma, la cuenta de cobro del mes anterior. Esta se entiende recibida en la fecha de su envío electrónico. EL CLIENTE la pagará dentro de los **[N] días calendario** siguientes a su fecha de emisión. EL CLIENTE es responsable de mantener actualizado el correo de facturación.»
  - SEXTA (b): «El consumo adicional de IA autorizado se incluye **en la misma cuenta de cobro** del mes en que se consumió, a la tarifa vigente publicada al momento del consumo.»
  - SEXTA (d): «La mora se cuenta desde el día siguiente al **vencimiento de la cuenta de cobro**. EL PRESTADOR podrá suspender el servicio tras aviso escrito con [N] días de anticipación.»
  - Anexo A 6(c): nombrar el proveedor SMTP.
- **Numeración y anulación:** una sola serie CC, consecutiva y sin reutilización. La anulación conserva el número y el PDF. Corregir es emitir otra CC.
- **Retención documental y almacenamiento:** las cuentas, los pagos y los PDF se conservan varios años (plazo que confirma el abogado o contador). El disco de Container Apps no es durable, así que en producción hacen falta Blob y cotización (D32). La BD guarda la foto completa: el PDF se puede regenerar y `PdfSha256` identifica el emitido.
- **Otros controles:**
  | Riesgo | Control |
  |---|---|
  | Fuga entre empresas | RLS FILTER + BLOCK solo superadmin; `fac.Alerta` solo superadmin; FK compuestas `(Id, EmpresaId)`; pruebas de aislamiento por PR |
  | Doble cuenta o doble envío | applock + `UX_FacCC_Periodo`; `fac.TareaProgramada`; `UX_FacEnvio_Hito` |
  | Número duplicado o con huecos | `usp_SiguienteNumero` dentro de la transacción + `UX_FacCC_Numero`; prueba concurrente |
  | Excedente IA mal cobrado | Línea apagada por defecto; evidencia `EnExcedente`; tope; retención por cuadre; 1042 |
  | Licencia QuestPDF | D33, revisión anual [H] |

## 35. Plan de implementación (Sonnet) y decisiones

Cada paso es un PR, compila sin advertencias y trae sus pruebas (regla 0.7). Antes de editar un símbolo existente se corre `impact` de GitNexus, y antes del commit `detect_changes` (CLAUDE.md).

| # | Paso | Archivos (capa) | Pruebas |
|---|---|---|---|
| 1 ✅ | DDL + RLS (hecho; prorrateo en líneas y condiciones) | `1040_facturacion.sql`; rama `fac` en `siempre/9000_rls.sql`; `FacturacionEsquemaTests.cs` | Integración: `sys.security_predicates` cubre `fac`; el `Admin{NIT}` de A no lee B ni escribe en `fac`; no ve `fac.Alerta`; sin contexto no ve nada; CHECK de estado y número |
| 2 | Procedimientos | `1041_facturacion_procedimientos.sql` | 20 emisiones concurrentes → números distintos y sin huecos; rollback sin consumo; sin sobreaplicación; un no superadmin recibe `THROW 50100` |
| 3 | Dominio | `Domain/Facturacion/*.cs` | Unitarias: transiciones, canon pactado vs plan, `ReglasEmision` (cada fila de §33), excedente, vencimiento, edades |
| 4 | Configuración comercial | contratos + comandos de canon, condición y parámetros + `RepositorioFacturacion` + páginas de planes y condiciones + políticas | No superadmin → `NoAutorizado`; solapamiento de vigencias rechazado; `aud` escrito |
| 5 | Programador + generación + emisión | `ProgramadorFacturacion.cs`, `GeneradorCuentas.cs`, manejadores `PREVALIDAR`/`GENERAR_*`; `fac.Alerta` | Reloj falso: día 1 06:00 → una cuenta por empresa; dos corridas → una; cada regla de retención crea `BORRADOR` + alerta; con el interruptor → `BORRADOR`; prevalidación sin escribir cuentas |
| 6 | PDF + descarga | `PdfCuentaCobro.cs`, `AlmacenDocumentosDisco.cs`, `EndpointsFacturacion.cs` | Bytes que empiezan con `%PDF`; el DTO no tiene campos de usuario ni de texto; descarga en `aud.Acceso`; el `Admin{NIT}` de B → 404 |
| 7 | Correo | `Correo/CorreoSmtp.cs`, `OpcionesCorreoSmtp`, modo `Archivo`, `ENVIAR_CORREO`, `RESUMEN_ALERTAS`, página de correo; instalar MailKit (versión confirmada) | `ICorreoSaliente` falso: transitoria → reintento; permanente → `FALLIDO` + alerta; ya enviado → no reenvía; `None` fuera de `Development` → falla al arrancar; **la prueba de PII en logs**; modo `Archivo` produce .eml con adjunto |
| 8 | Pagos y portal del cliente | comandos de pago, aplicación, reverso y anulación; páginas de pagos, `/facturacion/*` y contactos | Recaudo + retención → `PAGADA`; reverso → `EMITIDA`; referencia duplicada rechazada; el `Admin{NIT}` no ve borradores ni otra empresa |
| 9 | Mora, anulación y reemplazo | `REVISAR_MORA`; anular + reemplazar + correo `ANULACION` | Hitos idempotentes; recordatorios sin adjunto; anular con pagos → rechazado |
| 10 | (Al activar la IA a clientes) Excedente | `1042_ia_periodo_colombia.sql`, `RepositorioAsistente`, `CERRAR_PERIODO_IA`, línea IA en `GeneradorCuentas` | Llamada a las 23:30 Bogotá del día 31 → periodo M; el cierre es idempotente; tarifa cambiada a mitad de mes → 2 filas de detalle; las pruebas del asistente siguen verdes |

**Resueltas (registrar en `06`)**
- **D39:** una sola serie global CC, asignada al emitir, sin reutilización; la anulación conserva el número.
- **D40:** emisión y envío automáticos; interruptor «requiere aprobación» global y por empresa, apagado por defecto.
- **D42:** PDF adjunto solo para la cuenta de cobro.
- **D47:** mes vencido; generación y envío el día 1 a las 06:00 Bogotá.
- **D52:** sin DIAN, sin IVA, sin notas; la corrección es anular + reemitir.
- **D53:** SMTP genérico solo por configuración de despliegue; la clave en Key Vault o User Secrets, nunca en BD.
- **D48:** se prorratea por días calendario del mes en altas, terminaciones y cambios de condición a mitad de mes (`PRORRATEO = DIAS_MES` [S]); una línea por tramo.
- **D51:** el canon se cobra siempre, también con la empresa suspendida por mora. Confirmar con el abogado que el contrato lo respalda (P13).
- **D44:** el periodo de IA pasa a hora Colombia (1042), obligatorio antes de cobrar excedente.

**Eliminadas:** D43 (envío atado a la factura DIAN), D46 (proveedor de correo), D41 (desplazamiento de la numeración) y P12 (IVA).

**Abiertas (dueño)**
| # | Decisión | Recomendación |
|---|---|---|
| D50 | Línea de excedente IA: ¿desde cuándo? | Mantenerla apagada (`INCLUIR_EXCEDENTE_IA = 0`) hasta la fase 2 de IA con clientes y la tarifa publicada |
| D49 | `fac.consultar` para `FINANCIERO`/`TESORERO` | Solo `ADMIN_EMPRESA` en v1 (se cierra con P10) |
| D45 | Motivo de `aud.AccesoSuperadmin` para ver facturación | No exigirlo: son datos del prestador |

**Pendientes nuevos (proponer en `06` §10)**
| # | Pendiente | Responsable | Bloquea |
|---|---|---|---|
| P13 | Ajustar SEXTA (a), (b) y (d) y el Anexo A 6(c) con el texto de §34; fijar [N] días | Abogado + dueño | Primer cobro real |
| P15 | Cuenta SMTP, dominio o subdominio con SPF/DKIM/DMARC, secreto en Key Vault, costo (D32) | Dueño (depende de P8) | Primer envío real |
| P16 | Blob para los PDF (durabilidad y retención), con cotización | Dueño (D32) | Producción |
| P17 | Canon por plan y planes comerciales (amplía P11) | Dueño | Primer cobro real |
| P18 | Datos del emisor, instrucciones de pago y `CORREO_OPERACION` | Dueño | Paso 6 |
| P19 | Tarifa de IA publicada para el cliente (SEXTA (b)) | Técnico | D50 |
| P21 | Plazo de retención de cuentas, pagos y destinatarios | Abogado / contador | Política de borrado |

Fuente externa consultada (2026-11-05): NuGet.org `MailKit` (4.18.2, MIT, `net10.0`).
