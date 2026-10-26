# Notas de implementación — F2 · Ingesta

Complementa a `24_SPEC_MAESTRA.md` y `05_contrato_api_ingesta.md` (mandan). Aquí: qué se implementó, supuestos `[S]` y lo que necesita decisión.

## 1. Qué incluye F2
| Entregable (§13) | Dónde | Cómo se verifica |
|---|---|---|
| API de ingesta (`/v1/cargas`, lotes, cerrar, estado, rechazos) | `IAnalitica.Api` (`Endpoints.cs`, `Program.cs`), `Application/Ingesta` | `ApiIngestaTests` (16) con token real y SQL Server real |
| Token OAuth2 *client credentials* (OpenIddict, JWT 60 min, claim `empresa_id`) | `Api/Program.cs`, `Infrastructure/Ingesta/RepositorioApiClientes.cs` | `ApiIngestaTests` (token, secreto malo, cliente apagado, 401) |
| Límite por cliente (`LimitePorMinuto`) | `Api/Program.cs` (ventana fija por minuto, el límite viaja en el token) | 429 con `Retry-After`; otro cliente sigue pasando |
| Lotes gzip ≤ 5000 filas / 10 MB, idempotentes por número | `Endpoints.cs`, `RepositorioCargas.GuardarLoteAsync` | 413, 400, reintento del mismo lote |
| Cierre con control (lotes, filas, `sumaControl`), hash y detección de duplicados | `RepositorioCargas.CerrarAsync` | 422 `CONTROL_NO_CUADRA` (5 casos), 409 `CARGA_DUPLICADA` |
| Cola SQL `IColaTrabajos` | `Infrastructure/Ingesta/ColaTrabajosSql.cs` | `ColaTrabajosTests` (exclusión, concurrencia, lease, 1/2/4/8/16 min, `VENENO`) |
| Worker `PROCESAR_CARGA` y `CALCULAR_CARTERA` | `Infrastructure/Ingesta/Procesamiento.cs`, `Worker/ServicioTrabajos.cs` | `WorkerTests`, `CatalogosTests` (23), `HechosTests` (12) |
| Cartera (`etl.usp_GenerarCorteCartera`) | script 0150, manejador `CALCULAR_CARTERA` | `CarteraTests`: saldo, abonos, reversiones, edad por `FechaCartera`, cuadre con el libro |
| Procesamiento de asientos y facturas | `database/scripts/0160_ingesta.sql` | `HechosTests` |
| Procesamiento de los 16 catálogos | `DefinicionesCatalogo.cs` + `GeneradorCatalogoSql.cs` | `CatalogosTests` |

Prueba de compatibilidad con el conector real (F1): se ejecutó **una vez, de forma manual y sin commitear**, el núcleo `SaludIPSNet.Analitica.Nucleo` (`EjecutorAnalitica` + `ClienteIngesta`) contra esta API en proceso: 7 entidades `OK`; la segunda ejecución recibió `409 CARGA_DUPLICADA` y quedó en «sin cambios». Esa prueba no vive en el repositorio porque exigiría referenciar el código de SaludIPS2.

## 2. Criterio de aceptación de F2 (§13)
> «Carga de 13 meses del piloto; la cartera cuadra con `CARTERA ESTADO CUENTA EDADES` (≤ 0,5 %).»

**No se puede cerrar aquí.** No hay datos de ningún piloto. Lo que sí está probado: la lógica de cartera sobre escenarios sintéticos (abono parcial, pago total, abono reversado, edad desde `FechaCartera`, CxP, cuadre contra el saldo contable de la cuenta) y la carga de 12 000 filas en 3 lotes. Falta correr, con el piloto, la comparación contra el reporte de SaludIPS y medir tiempos de una carga de 13 meses.

## 3. Entidades aceptadas en F2
Catálogos (16), `asientos` y `facturas` (VENTANA, INICIAL e INCREMENTAL para anulaciones). El resto del contrato (`recibos`, `inventario`, `conciliaciones`, `glosas`, operativas, `presupuesto`…) y `configuracion` (auditor, F4) se rechazan con `400 ENTIDAD_NO_SOPORTADA`: no se acepta un lote que nadie va a procesar. Con el conector actual, `configuracion` (si está habilitada) falla cada noche con ese código hasta F4; las demás entidades no se ven afectadas.

## 4. Supuestos `[S]`
1. **`GET /v1/estado`** → `{ empresa: <NIT>, entidades: [{ entidad, ultimaCargaOkEn, cargaId }] }`. Las anulaciones de facturas (`facturas` en modo INCREMENTAL) se exponen como la entidad `facturas-anulaciones`, que es como las vigila el conector.
2. **Carga duplicada** = mismo hash de (entidad, modo, ventana/corte, contenido de cada lote). Se responde `409 CARGA_DUPLICADA` al cerrar; la carga queda `RECHAZADA` con ese error y **cuenta para la marca de agua** de `/v1/estado` (confirma que los datos siguen al día). Cerrar es idempotente: repetirlo devuelve el mismo resultado.
3. **`sumaControl` obligatoria** para `asientos` (Σ débito + Σ crédito) y `facturas` (Σ `valorNeto` de todas las filas enviadas, anuladas incluidas); tolerancia 0,01. Sin ella: `422`.
4. **Una fila rechazada conserva su versión anterior**: el reemplazo de ventana borra solo lo que el origen *no envió*, no lo que falló la validación.
5. **Un comprobante con alguna línea rechazada se rechaza completo** (`COMPROBANTE_INCOMPLETO`), igual que el descuadrado: nunca entra contabilidad parcial.
6. **Filas fuera de la ventana** de una carga VENTANA/INICIAL se rechazan (`FUERA_DE_VENTANA`): la ventana define qué se reemplaza.
7. **Referencias:** requeridas inexistentes → rechazo `REFERENCIA_INEXISTENTE:<campo>`; opcionales inexistentes → `NULL` (05 §4).
8. **«Filas ausentes → inactivas» (COMPLETA)** solo donde la tabla tiene bandera de activo (sedes, centros de costo, pagadores, almacenes, profesionales, usuarios operativos). En el resto no se hace nada.
9. **Un tercero paciente con `nombre`** invalida **toda** la carga (05 §4): `422 VALIDACION_NEGOCIO`, carga `RECHAZADA`, sin guardar el lote; el nombre no se devuelve en la respuesta. Defensa en profundidad: el procesador también fuerza `Nombre = NULL` si `esPaciente`.
10. **Cartera:** se encola al terminar una carga de `asientos`, con corte = hoy en Colombia (UTC−5). Trabajos consolidados por empresa (`EMP:n|CARTERA`).
11. **Exclusión de trabajos:** la clave es `EMP:n|CARGA:id` (una por carga, para no descartar cargas distintas); la serialización por empresa y entidad la hace un *applock* de SQL Server dentro del procesador. Si está ocupado, el trabajo se pospone 30 s **sin consumir intento**.
12. **Prioridad:** catálogos 3, asientos 5, facturas 6, cartera 7.
13. **Datos del staging:** se borran al terminar la carga en `OK`; con `ERROR` se conservan para diagnóstico.
14. **Mensajes de error de procesamiento** (`etl.Carga.Error`, `etl.Trabajo.Error`) guardan solo el tipo de excepción o el número de error SQL, nunca valores de filas.
15. **`etl.Rechazo.Payload`** no se usa (queda `NULL`): el motivo y la llave bastan y evita guardar datos en un lugar de diagnóstico.
16. **Llaves de firma del token:** efímeras (una sola instancia). En Azure salen de Key Vault (F6); con varias instancias de la API hay que compartirlas.
17. **Periodo cerrado** (`409 PERIODO_CERRADO`): se consulta `etl.PeriodoCierre`; hoy nada lo cierra (llegará con el motor de costos, F5).

## 5. Para decidir (diseño)
1. **Transacción única por carga vs. lotes cortos.** 24 §8/spec 20 §3 piden «transacciones cortas» *y* «atómico por carga». F2 elige atómico (una transacción por carga, con `XACT_ABORT`). Una carga INICIAL de un mes con millones de líneas crece el log y bloquea; si la medición del piloto lo exige, hay que partir por día con un marcador de avance (cambia la semántica de «todo o nada»).
2. **`LimitePorMinuto` por defecto = 60.** Una carga inicial de ~2 M líneas son ~400 lotes: el conector esperaría `Retry-After` de 60 s repetidamente. Se sugiere 600 para clientes de ingesta (se puede ajustar con un `UPDATE` o al crear la empresa).
3. **Estados `ERROR` de cargas** no se reintentan solas más allá de `MaxIntentos` (5); queda `VENENO` en la cola y hace falta alerta (F3/F5: `ALERTAS`).
4. **Contrato `GET /v1/estado`** y los DTO viven en `IAnalitica.Ingesta.Contracts` (netstandard2.0). El conector tiene hoy una copia en su núcleo; cómo se comparte (NuGet privado vs. copia verificada por pruebas) sigue abierto.
5. **`configuracion` (FOTO)** necesita tablas destino (`saludips.*`, F4). Mientras tanto se rechaza.

## 6. Hallazgos durante las pruebas (corregidos)
- Las tablas temporales usan la intercalación de `tempdb`; las dimensiones, `Modern_Spanish_CI_AS` → *collation conflict*. Todas las columnas de texto de las temporales llevan `COLLATE DATABASE_DEFAULT`.
- `etl.Trabajo.DisponibleEn` es `datetime2(0)` y **redondea hacia arriba**: un trabajo recién encolado podía quedar medio segundo en el futuro. La toma admite 1 s de holgura.
- Con la RLS, `seg.Empresa` no se ve en modo autenticación: la comprobación «empresa activa» al emitir el token se hace en un segundo paso con el contexto de la propia empresa.
- OpenIddict responde `invalid_client` con **401** (no 400).
- La 10.0.11 de `System.Security.Cryptography.Xml` (transitiva de OpenIddict) tiene vulnerabilidades conocidas (NU1903): se fija 10.0.14 en el proyecto de pruebas; la API la recibe del *shared framework*.
