# Infraestructura de IAnalitica: VPS vs Azure

Estado: **decidido: Azure** (D19). Región pendiente.

## Recomendación: **Azure**, empezando pequeño
| Criterio | VPS | Azure (PaaS) |
|---|---|---|
| **Base de datos** | SQL Server instalado por ti. Express limita a **10 GB por BD**: alcanza para los pilotos, no para 50 instituciones. Standard en un SaaS multicliente requiere licenciamiento por suscripción (SPLA) mediante el proveedor del VPS | **Azure SQL Database**: RLS, columnstore, respaldos automáticos con restauración a un punto en el tiempo, cifrado en reposo y escalado sin reinstalar |
| Respaldos y recuperación | Tuyos (scripts, almacenamiento externo, pruebas de restauración) | Incluidos y configurables |
| Seguridad y parches | Tuyos (Windows, IIS, SQL, firewall) | El proveedor parchea la plataforma; tú configuras |
| Secretos | Archivos o variables en el servidor | **Key Vault** |
| Monitoreo y logs | Por montar | **Application Insights** y alertas |
| Escalar de 3 a 50 instituciones | Migrar de servidor | Cambiar de plan |
| Costo inicial | Más bajo | Más alto, controlable con planes pequeños y presupuesto con alertas |
| Riesgo para datos de salud | **Alto** si no hay un administrador dedicado | Menor |

**Conclusión:** un VPS solo sirve para pruebas internas. Con datos de salud de terceros, 3 instituciones en producción y crecimiento a 50, los respaldos, la seguridad y la base de datos administrada de Azure valen la diferencia.

## Etapa de pruebas: costo mínimo (regla D32)
Objetivo: desarrollar y probar con los 3 pilotos **sin gasto fijo relevante**. Los cupos gratuitos que siguen salen de fuentes secundarias y de la página de precios de Container Apps (oct‑2026). **Se verifican en la página oficial de Azure, con fecha, antes de crear cada recurso, y se presentan para tu aprobación.**

| Componente | Opción de pruebas | Cupo gratuito reportado (verificar) | Limitación a tener en cuenta |
|---|---|---|---|
| Base de datos | **Azure SQL Database, oferta gratuita** (General Purpose serverless) | Hasta 10 BD; cada una con 100.000 vCore‑segundos y 32 GB al mes | Al agotar el cupo: **configurar «pausar hasta el próximo mes»** (no «seguir cobrando»). La BD se pausa sola cuando no se usa y la primera consulta tarda en despertar |
| API, worker y portal | **Azure Container Apps** (consumo, escala a cero) | 180.000 vCPU‑s, 360.000 GiB‑s y 2 millones de solicitudes al mes por suscripción | Arranque en frío tras inactividad; aceptable en pruebas |
| Logs y trazas (OpenTelemetry) | Azure Monitor / Log Analytics | 5 GB al mes por cuenta de facturación | **Muestreo** y nivel de log controlados (ver spec 19); retención corta |
| Secretos | Key Vault | Se cobra por operación: centavos al mes | — |
| Archivos | Blob Storage (LRS, capa caliente) | Centavos con volumen bajo | — |
| IA | Claude Haiku 5.5 | Se paga por uso (USD 0,10 / 0,50 por millón de tokens) | Cupo de plataforma bajo (por ejemplo, USD 5 al mes) en `ia.EmpresaConfig.TopeCostoProveedorUsd` |
| Landing | SmarterASP (ya pagado) | — | — |

**Controles obligatorios desde el día 1:**
- presupuesto mensual de la suscripción con alertas al 50 %, 80 % y 100 %, por ejemplo de **USD 20** en pruebas;
- etiquetas por ambiente;
- revisión semanal del análisis de costos.
- **Prohibido en pruebas:** SQL provisionado, planes Premium, IP públicas dedicadas, Application Gateway o Front Door, Defender for Cloud de pago y réplicas geográficas.

**Cuándo escalar:** cuando un piloto pase a producción con contrato firmado. El paso a un nivel pago se presenta antes con su cotización (D32).

## Arquitectura de producción (cuando haya contrato; con cotización previa)
| Componente | Servicio | Nota |
|---|---|---|
| API de ingesta, portal Blazor, worker | **Azure Container Apps** (misma plataforma que en pruebas, con réplicas mínimas) | Dominio `app.<dominio>` y `api.<dominio>` con TLS gestionado |
| Base de datos | Azure SQL Database, vCore General Purpose (2 vCores) o serverless en pilotos | Escalar al pasar de los pilotos |
| Archivos (logos, consentimientos firmados, exportaciones) | Azure Blob Storage | Privado; acceso con URL temporales |
| Secretos | Azure Key Vault | Claves de Anthropic, cadena de conexión, firma de tokens |
| Observabilidad | Application Insights + Log Analytics | Ver spec de logs pendiente |
| Correo transaccional | Azure Communication Services u otro proveedor SMTP | Alertas, resumen semanal |

- **Región:** no hay región de Azure en Colombia. Opciones: Brazil South, Chile Central o Mexico Central, o East US 2. **Verificar** la disponibilidad de Azure SQL Database y el precio en la región elegida, y que el **abogado confirme** el régimen de transferencia internacional (Ley 1581) para la región escogida.
- **Costo:** estimarlo con la calculadora oficial de Azure. Activar presupuesto y alertas de costo desde el día 1. Revisar programas de créditos para empresas nuevas de Microsoft (sus condiciones cambian).

## Landing en SmarterASP
Funciona para un sitio informativo, con estas condiciones:
1. HTTPS obligatorio.
2. El formulario de demo **no guarda datos personales en el hosting compartido**: envía a la API de IAnalitica (`POST /v1/prospectos`) o a un correo seguro.
3. Aviso de privacidad, autorización de datos y banner de cookies (ver `legal/`).
4. Enlace «Ingresar» hacia `app.<dominio>` en Azure.
