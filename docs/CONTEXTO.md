# IAnalitica — CONTEXTO (índice)

Estado: rama `feature/f5-costos-ia` (PR #7). F0–F5 implementadas; F6 sin iniciar. Actualizado: 2026-11-05.
Contexto vigente para humanos y agentes de IA, partido por tema. La numeración §N es global. **Cargar solo el archivo que aplique.**
Precedencia: este contexto → código y `database/scripts` (el DDL manda) → `docs/archivo/` (histórico, no usar como fuente).

| Archivo | Secciones | Cargar cuando |
|---|---|---|
| `contexto/01_reglas_arquitectura_seguridad.md` | §0–§3 | Siempre al empezar: reglas, producto, arquitectura, stack, convenciones, identidad, permisos, RLS |
| `contexto/02_datos_ingesta_conector.md` | §4, §5, §15, §16 | Tocar BD, scripts DbUp, API de ingesta, cola, contrato de entidades, conector SaludIPS |
| `contexto/03_modulos_dashboards_kpi.md` | §6, §17, §18, §20, §21, §22 | Tocar dashboards, métricas, KPI, costos, farmacia/precios, auditor, ESE |
| `contexto/04_asistente_ia.md` | §7, §19 | Tocar el asistente, cupo, tarifa, proveedor |
| `contexto/05_infraestructura_operacion.md` | §8, §23–§26 | Azure, costos, CI, observabilidad, colas, reportes |
| `contexto/06_plan_pendientes_decisiones.md` | §9–§13 | Planear: fases, pendientes, riesgos, decisiones D01–D38, divergencias spec↔código |
| `contexto/07_usuarios_landing.md` | §27, §28 | Alta de empresas/usuarios, landing |
| `contexto/08_facturacion_cuenta_cobro.md` | §29–§35 | Cobro mensual del SaaS: cuenta de cobro, pagos, mora, correo SMTP (esquema `fac`) |

Fuera de este contexto (siguen aparte): `02_mapeo_SaludIPS.md`, `03_analisis_reportes_SaludIPS.md`, `06_extraccion_saludips.sql`, `11_extraccion_operativa_saludips.sql`, `08_consentimiento_proveedor_ia.md`, `30_DIAGNOSTICO_SIPS108_vs_SIPS550.md`, `legal/`, `fuentes-saludips/`.
