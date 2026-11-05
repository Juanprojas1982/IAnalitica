# IAnalitica — Plan por fases, pendientes, riesgos, decisiones D01–D38, divergencias

Parte de `docs/CONTEXTO.md` (índice). La numeración §N es global y se conserva entre archivos. Precedencia y reglas de uso: `01_reglas_arquitectura_seguridad.md` §0.

## 9. Plan por fases
| Fase | Estado | Criterio de aceptación | ¿Cerrado con datos reales? |
|---|---|---|---|
| F0 Base | ✅ | 2 empresas; `Admin{NIT}` de A no ve B | Sí (prueba automatizada) |
| F1 Conector | ✅ simulación | JSON cuadran contra `SIPS_5` y `ESTADORESULTADOSCENTROCOSTOHORIZONTAL` | No |
| F2 Ingesta | ✅ | 13 meses; cartera ≤ 0,5 % vs `CARTERAESTADOCUENTAEDADES` | No |
| F3 Dashboards | ✅ parcial | Los 3 pilotos los usan | No |
| F4 Auditor y validador | ✅ parcial | ≥ 80 % de hallazgos confirmados reales | No |
| F5 Costos e IA | ✅ parcial | Conservación del costo; consumo IA dentro del tope | Conservación y tope: sí (sintético). Pilotos y modelo real: no |
| F6 Despliegue piloto | ⏳ | 95 % de noches con carga correcta durante 30 días | — (bloqueada por P1 y por datos reales) |

## 10. Pendientes y decisiones abiertas
### Técnicos (próximos)
1. Adaptador Anthropic (`IAsistenteChatClientFactory`): instalar y probar el paquete `Anthropic`; requiere clave y aprobación D32.
2. Ingesta de `nomina`, `prestaciones`, `movimientos-inventario` (bases COSTO_PERSONAL, ACTIVIDADES, ATENCIONES, CONSUMO_INSUMOS) y de los hechos de agenda, farmacia, tesorería (`recibos`), presupuesto, admisiones/prestaciones. Hoy se rechazan con `ENTIDAD_NO_SOPORTADA`.
3. Clasificación de centros de costo (`TipoCC`) y carga de bases manuales de costos.
4. Cierre de periodo (`etl.PeriodoCierre`) y trabajo `ALERTAS`; alerta para `VENENO`; llamar `ia.usp_CerrarPeriodo`.
5. Bajada del catálogo regulatorio a SaludIPS y validación en tiempo real; reglas R3, R4, R7–R9; descarga automática del dataset `nauz-qkjw` (campos por validar).
6. Resto del auditor (CON-03, CON-05…09, TAR-03…05/07/08, FE-04/05, AGE, ALM, PAR, USU-02/03); parte CxP de CON-04; resumen semanal por correo (sin proveedor de correo, D32).
7. Asistente: streaming SSE, panel `Ctrl+K`, valoración 👍/👎, evaluaciones (`ia.Modelo.EvaluacionAprobada`), caché de prompt, conteo exacto de tokens, resumen de historial, `ModeloRespaldoId`, consumo visible y alertas 70/90 %.
8. Contrato 05: agregar `tarifas-suministros`, `catalogo-medicamentos`, `ordenes-externas`, `presupuesto-publico`, `contratos-publicos` con tablas destino (P9). Compartir DTO `Ingesta.Contracts` con el conector (NuGet privado vs copia verificada por pruebas).
9. Selector de empresa del superadmin con motivo y registro `aud.AccesoSuperadmin` (no existe); dashboards del superadmin.
10. Almacenamiento de reportes compartido o Blob si hay varios nodos (`Reportes:Almacen:Ruta`); llaves de Data Protection y de firma de token en Key Vault (F6).
11. Gráficos en PDF (hoy tablas y KPI); logo en PDF; QR del 2FA.

### Decisiones de diseño abiertas
- **Render:** `InteractiveAuto` + MudBlazor (spec original) vs. SSR+SVG actual (F3). Decidir si se quiere interactividad (requiere proyecto `IAnalitica.Web.Client` no previsto en la estructura).
- Usuarios únicos globales vs. sufijo por empresa.
- Transacción única por carga vs. partir por día si la carga inicial de ~2 M líneas lo exige.
- Dashboard gerencial propio vs. widgets de otros módulos (`ModulosAdicionales`).
- Exportador de Azure Monitor y su límite de ingesta; licencias de Mapperly y ApexCharts.
- Permiso propio `costos.configurar`.

### Para el dueño / equipo funcional
| # | Pendiente | Responsable | Bloquea |
|---|---|---|---|
| P1 | Región de Azure | Dueño + abogado | F6 |
| P2 | Validar `[S]` de 06, 11 y 15 (fechas de atención, `VrCosto`, `DiaHorario`, `TipoPersona`, `ClaseTercero`, `TipoCentroCosto`, `EstadoEntidad = 0`, factores por CUM) | Equipo funcional | Métricas que los usan |
| P3 | Cobertura de `dFacturasServicios` | Técnico | Enlace de facturas (hay respaldo) |
| P5 | Revisión y firma de documentos legales | Abogado | Contratos y pilotos |
| P6 | Fórmulas de riesgo de la ESE | ESE piloto | Simulador (fase 2) |
| P7 | Reglas de distribución de costos por piloto (centros, orden, base) | Cada piloto | Cierre de F5 |
| P8 | Dominio y marca | Dueño | Landing |
| P10 | Confirmar matriz rol × permiso | Dueño | Nada (script nuevo) |
| P11 | Planes de IA, tarifa de venta (COP por millón de tokens), cupo, autorización de excedente, visibilidad del costo | Dueño | IA a clientes |
| — | ¿`CodPlan` es parte del cruce de tarifas? ¿`pSuministros.Costo` y `Valor` son por unidad o por presentación? ¿Tabla de CxP para CON-04? ¿Ruta del menú SaludIPS por hallazgo? ¿Campos del dataset `nauz-qkjw`? | Equipo funcional | TAR-02, validador, CON-04 |
| — | Homologación `TipoOrigen` → administrativo/apoyo/productivo | Equipo funcional | Reglas por defecto de costos |
| — | Diagnóstico SIPS_108 vs SIPS_550_1: ejecutar el script de §4 de `30_DIAGNOSTICO_SIPS108_vs_SIPS550.md` contra una base real; causa probable (sin confirmar) = INNER JOIN a la tarifa del manual | Técnico | — |

## 11. Riesgos
| Riesgo | Control |
|---|---|
| Cifras que no cuadran | Cuadre contra reportes de SaludIPS antes de mostrar un dashboard a un cliente |
| Falsos positivos en auditor/validador | Reglas de alta certeza, `FALSO_POSITIVO`, factores validados |
| Fuga entre empresas | RLS + filtro en handlers + pruebas por PR |
| Costo de nube o IA | D32, presupuesto con alertas, topes de IA, cupo gratuito con pausa |
| Licencias comerciales | Verificar al instalar; QuestPDF revisión anual (umbral USD 1 M) |
| Alcance creciente | Lo nuevo entra como fase posterior |
| Ramas largas sin integrar | PR #7 `feature/f5-costos-ia` → `main` |

## 12. Decisiones (registro)
| # | Decisión |
|---|---|
| D01 | Producto IAnalitica, solo sector salud |
| D02 | Paga la institución, cada una por su cuenta |
| D03 | La implementación incluye configuración, cuadre, reglas y capacitación |
| D04 | 3 pilotos: operador farmacéutico, IPS privada, ESE |
| D05 | Landing sin precios (solo «Cotizar»), independiente del HIS; caso SaludIPS sin cifras de mejora hasta medir y autorizar |
| D06 | Landing en SmarterASP con dominio propio; la plataforma se aloja aparte |
| D10 | .NET 10, Clean Architecture, CQRS con mediador propio, EF Core, SQL Server, Blazor. Repo `Juanprojas1982/IAnalitica` |
| D11 | BD central multiempresa con `EmpresaId` + RLS; usuario de una sola empresa; superadmin ve todas |
| D12 | ASP.NET Core Identity (no Entra); 2FA obligatorio para administradores |
| D13 | Ingesta por API OAuth2 client credentials; en el conector «usuario/contraseña» = ClientId/ClientSecret |
| D14 | Conector dentro de SaludIPS (`SaludIPSNet.Analitica.Conector`, net48, servicio de Windows); SaludIPS es on-premise con salida a internet |
| D15 | Los parámetros de SaludIPS se copian solos a la nueva vigencia |
| D16 | Dashboards declarativos sobre capa semántica; perfiles por módulos activos |
| D17 | Auditor de configuración: solo lectura, sugiere y hace seguimiento |
| D18 | Validador de precios máximos con catálogo regulatorio central |
| D19 | Hosting Azure; región pendiente |
| D20 | Anthropic: Haiku 5.5 en pruebas, Sonnet 5.5 en producción; sin proveedores chinos |
| D21 | Cobro IA: cupo en el canon + excedente a tarifa configurable con tope |
| D22 | OpenTelemetry → Azure Monitor con muestreo; auditoría de negocio en `aud.*` |
| D23 | Cola detrás de `IColaTrabajos`; pilotos con cola SQL; Storage Queues solo con cotización |
| D24 | Reportes imprimibles/descargables siempre en segundo plano |
| D25–D27 | Solo el superadmin crea empresas; contraseña inicial aleatoria de 14 caracteres (72 h, cambio y 2FA obligatorios); un solo `Admin{NIT}` por empresa |
| D30 | Diseño con Opus, implementación con Sonnet |
| D31 | Documentos legales: borradores que revisa y firma un abogado |
| D32 | Costo primero: precio oficial con fecha + estimado mensual + aprobación explícita |
| D33 | Licencias comunitarias: la empresa factura < USD 1 M/año (declarado por el dueño, 2026-10-26); QuestPDF comunitaria |
| D34 | Mediador propio, sin MediatR; preferir componentes .NET o MIT/Apache sin vencimientos |
| D35 | RLS por `SESSION_CONTEXT`; desactivación controlada por la aplicación; dos roles de BD |
| D36 | RLS en script que corre en cada despliegue |
| D37 | Matriz rol × permiso `[S]` |
| D38 | Primer superadmin por comando del migrador; plan `PILOTO` con cupo 0 `[S]` |

## 13. Divergencias spec original ↔ implementación (la implementación manda)
| Spec original | Realidad |
|---|---|
| Dashboards con MudBlazor/ApexCharts, `InteractiveAuto` | SSR + estado en URL + SVG, sin JavaScript |
| Descarga de reportes por SAS de 15 min en Blob | Disco local, descarga por el portal con sesión |
| Diez herramientas fijas del asistente | Dos herramientas genéricas sobre el catálogo semántico |
| Dashboard `auditor_config` en la capa semántica | Página dedicada `/auditor` (permiso `dash.auditor_config` sí aplica) |
| Cadena nocturna incluye `DISTRIBUIR_COSTOS` y `ALERTAS` | Existe `CALCULAR_COSTOS` (por mes); `ALERTAS` no existe |
| Exportador Azure Monitor | Consola/OTLP |
| README y menciones a MediatR | No se usa MediatR |

