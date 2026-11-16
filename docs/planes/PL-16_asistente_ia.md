# PL-16 · Asistente IA: proveedor real y salida a clientes

Estado: diseñado, **bloqueado por D32 (costo) y P11**. Scripts `1180_asistente.sql` y el reservado `1042` (excedente, D50).
Cierra los pendientes técnicos 1 y 7 (`06` §10).

## Objetivo
Pasar de «probado con modelo simulado» a un asistente que responde con un modelo real, medido con evaluaciones, con consumo visible y, solo entonces, habilitable para clientes.

## Situación actual
- Orquestador, herramientas (`listar_metricas`, `consultar_metrica`), cupo, reserva/liquidación y seudonimización: implementados y probados con modelo simulado.
- Sin `IAsistenteChatClientFactory` real responde «no hay un proveedor de IA configurado».
- `Microsoft.Extensions.AI.Abstractions` 10.11.0 instalado. El paquete `Anthropic` 13.3.0 «existe en NuGet; **no instalado ni probado**».
- Precios de `ia.Modelo` a 2026-10-06, por verificar. Plan `PILOTO` con cupo 0 `[S]`.

## Requisitos previos (no son código)
1. **D32:** precio oficial con fecha + estimado mensual + aprobación explícita del dueño para la clave de API. En pruebas, el menor gasto posible (tope de plataforma en USD ya soportado por `ia.usp_ReservarCuota`).
2. **Verificar antes de instalar** (no asumir): versión y licencia del paquete `Anthropic` en nuget.org, que implemente `IChatClient` de la versión de `Microsoft.Extensions.AI` del proyecto, y los identificadores y precios vigentes de los modelos en la documentación oficial. Si el paquete no expone `IChatClient`, escribir el adaptador sobre `HttpClient` contra la API de mensajes.
3. **P11/P19:** tarifa de venta, cupo por plan, quién autoriza el excedente, visibilidad del costo.
4. Consentimiento y política de tratamiento revisados (P5): el proveedor es un encargado nuevo.

## Fases
### Fase 1 · Adaptador (interno)
- Infrastructure `Asistente/FabricaClienteAnthropic.cs`: implementa `IAsistenteChatClientFactory`; modelo e identificador desde `ia.Modelo` (nada fijo en código); clave desde Key Vault / User Secrets (`IA:Anthropic:ApiKey`), nunca en `appsettings` ni BD.
- Comportamientos que el contexto ya fija (`04` §19): `effort` configurable por modelo; **`stop_reason = refusal` se maneja en la aplicación** con mensaje claro (no hay fallback del servidor); límite de historial 15K tokens.
- Conteo de tokens real desde `usage` de la respuesta (hoy se estima 3 caracteres/token para reservar; se liquida con el real). Registrar `cache_read_input_tokens`.
- Caché de prompt: orden herramientas → sistema → contexto de empresa → conversación; verificar que el uso de caché aparece en `usage`.
- `ModeloRespaldoId`: usar el segundo modelo ante error de disponibilidad; un error de autenticación o de cuota no reintenta.
- Sin registrar preguntas ni respuestas en logs (regla de `01` §2).
- Pruebas: unitarias con un `HttpMessageHandler` falso (sin red); una prueba de humo real, **marcada como manual y excluida del CI**, para no gastar ni exponer la clave.

### Fase 2 · Evaluaciones (condición para salir)
- Proyecto `tests/IAnalitica.Evaluaciones` (no corre en CI por defecto): 50–100 preguntas por dashboard con respuesta calculada por SQL sobre la empresa de prueba de `DatosDashboards`.
- Métricas: cifra correcta (± 0,5 %), herramienta correcta, «no sé» cuando falta el dato o el módulo, costo y latencia.
- Resultado en `ia.Modelo.EvaluacionAprobada` (ya existe); el orquestador **rechaza** un modelo sin evaluación aprobada para empresas que no sean la interna.
- Casos de seguridad obligatorios: instrucciones dentro de un nombre de tercero o de pagador (inyección por datos), petición de datos de otra empresa, petición de nombres reales (debe ver seudónimos), métrica de nómina con rol sin permiso.

### Fase 3 · Experiencia
- Streaming SSE en `POST /q/asistente/conversaciones/{id}/mensajes` (manda `/q/`, no `/v1/`). Como la interfaz es SSR sin JavaScript (divergencia de `06` §13), el streaming exige un mínimo de script o un componente interactivo: **decisión de render abierta** (`06` §10). Alternativa sin JS: respuesta completa con indicador de espera (lo que hay hoy). No implementar el panel `Ctrl+K` hasta decidir.
- Consumo visible (barra de cupo, valor de la respuesta según `VisibilidadCosto`), alertas al 70 y 90 % (reutiliza `ALERTAS` de PL-04), 👍/👎 por respuesta (tabla nueva `ia.Valoracion` en 1180), resumen de historial al pasar el tope.
- Fuentes: cada cifra enlaza al dashboard de su métrica con los filtros usados.

### Fase 4 · Clientes
- `ia.usp_CerrarPeriodo` invocado por una tarea mensual (hora Colombia, D44).
- Excedente y su cobro: paso 10 de facturación, script 1042 (D50) — ya diseñado en `08`; se activa aquí.
- Habilitación por empresa: consentimiento firmado, tarifa cargada, cupo del plan.

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1180_asistente.sql` (valoraciones), `1042` (D50) |
| Infrastructure | `Asistente/FabricaClienteAnthropic.cs` (nuevo), `DependencyInjection.cs`, `IAnalitica.Infrastructure.csproj` (paquete, tras verificar) |
| Application | `Asistente/*` (refusal, respaldo, valoraciones) |
| Web | `Components/Pages/Asistente.razor`, endpoints `/q/asistente/*` |
| Pruebas | `Application.Tests`, `Integration.Tests/Ingesta/AsistenteTests.cs`, `Web/AsistenteWebTests.cs`, `tests/IAnalitica.Evaluaciones` (nuevo) |

Símbolos para `impact`: `IAsistenteChatClientFactory`, el orquestador del asistente, el seudonimizador, `ia.usp_ReservarCuota`/`usp_LiquidarReserva` (texto).

## Riesgos
- **Costo:** Sonnet ≈ USD 0,05 por pregunta según el estimado del contexto (sin medir); un cupo mal fijado se nota en el margen. Medir con tráfico real antes de fijar P11.
- **Cifras inventadas:** la regla «toda cifra sale de una herramienta» solo se garantiza con evaluaciones; por eso la fase 2 es condición.
- **Inyección por datos:** los resultados de herramientas contienen texto de la IPS (nombres de pagadores, descripciones). Herramientas de solo lectura y máximo de iteraciones ya existen; las pruebas de la fase 2 lo verifican.
- **Datos personales hacia un tercero:** hoy solo `usuario` y `tercero` se seudonimizan; PL-11 y PL-12 agregan `profesional`. Toda dimensión nueva que nombre personas debe entrar a esa lista antes de exponerse al asistente.
