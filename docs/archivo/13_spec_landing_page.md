# Spec: Sitio web / landing page del producto

Estado: diseño (Opus). Implementación: Sonnet.
Producto: **IAnalitica**. Falta definir dominio e identidad visual.

## 1. Objetivo
Convertir visitas de gerentes, directores financieros y dueños de IPS en **solicitudes de demo o diagnóstico gratuito**, y servir de referencia comercial para los 30 clientes potenciales de SaludIPS.
Métricas: tasa de conversión a demo, demos que pasan a piloto y origen del tráfico.

## 2. Público y mensaje
| Público | Lo que le duele | Mensaje |
|---|---|---|
| Gerente o dueño de IPS | No sabe qué servicio o EPS le da o le quita plata | «Sepa qué servicio, qué EPS y qué sede le genera utilidad, con su costo real.» |
| Director financiero | Cartera, glosas y caja; reportes que se arman a mano | «Vea dónde está detenido su dinero: sin facturar, sin radicar, sin pagar o glosado.» |
| Director médico o de operaciones | Oportunidad, inasistencia, agendas ociosas | «Llene sus agendas y reduzca la inasistencia con datos, no con intuición.» |
| Grupo con varias IPS | Consolidación manual | «Todas sus empresas en un solo tablero, cada una con sus usuarios.» |

**Promesa central:** *«Decisiones gerenciales para IPS con los datos que su HIS ya tiene.»*
**Regla de contenido:** no publicar cifras de ahorro o ROI que no se hayan medido en clientes reales. Los resultados se publican solo con autorización del cliente.

## 3. Mapa del sitio
```
/                     Inicio (landing principal)
/funcionalidades      Detalle por módulo (anclas por sección)
/asistente-ia         Analista financiero con IA
/integracion          Cómo se conecta (SaludIPS y otros HIS), seguridad de datos
/planes               Planes y cupo de IA incluido
/seguridad            Privacidad, Ley 1581, anonimización, alojamiento
/recursos             Artículos (glosas, Circular 030, RIPS/CUV, costeo en IPS) → SEO
/demo                 Formulario de demo / diagnóstico
/legal/privacidad, /legal/terminos
/ingresar             → portal de la aplicación
```

## 4. Página de inicio: secciones en orden
1. **Encabezado:** logo, menú (Funcionalidades, IA, Integración, Planes, Seguridad, Recursos) y botones «Ingresar» y **«Solicitar demo»**.
2. **Hero:** la promesa central, un subtítulo con tres resultados (rentabilidad real, dinero detenido, agendas productivas), los botones **«Solicitar demo»** y «Ver cómo funciona» (video de 90 s), y una captura del dashboard gerencial con **datos ficticios**.
3. **Problema** en tres tarjetas: «Facturó, pero no sabe si ganó», «Su dinero está detenido y no sabe dónde» y «Paga horas de agenda que nadie usa».
4. **Funcionalidades** en una grilla de 8 tarjetas que enlazan a `/funcionalidades#…`:
   - **Tablero gerencial:** ingresos, margen, caja, cartera y alertas en una sola pantalla.
   - **Rentabilidad real:** P&G por sede, centro de costo, servicio y EPS, con distribución de costos de apoyo.
   - **Ciclo del ingreso:** atendido sin facturar, facturado sin radicar, rechazos de factura electrónica, días por etapa y responsable.
   - **Cartera y glosas:** antigüedad por EPS, días de cartera, glosas por causa y conciliación con las EPS.
   - **Agendas y oportunidad:** ocupación, inasistencia y su costo, cancelaciones, oportunidad por especialidad y capacidad ociosa.
   - **Productividad:** por profesional, especialidad y personal administrativo, con costo por hora.
   - **Inventario y farmacia:** rotación, vencimientos por lote, consumo no facturado y precios de compra.
   - **Presupuesto y finanzas:** ejecución presupuestal, balance, razones financieras y flujo de caja proyectado.
5. **Asistente IA:** una animación del panel lateral respondiendo una pregunta real con su fuente, y los mensajes «Pregunte en español, responde con sus cifras» y «Consumo controlado por empresa». Enlace a `/asistente-ia`.
6. **Multiempresa:** un diagrama del grupo con varias IPS, cada una con sus usuarios y su marca (logo y color), y la vista consolidada para la dirección.
7. **Cómo funciona,** en tres pasos: conectar (instalar el conector, sin abrir puertos), validar (cuadre contra sus propios reportes) y decidir (dashboards y alertas desde el día 1).
8. **Seguridad:** datos de pacientes anonimizados, aislamiento por empresa, cifrado y cumplimiento de la Ley 1581, con enlace a `/seguridad`.
9. **Para quién:** IPS ambulatorias, de diagnóstico y hospitalarias, cada una con su paquete de tableros.
10. **Planes:** un resumen con el botón «Ver planes».
11. **Preguntas frecuentes:**
    - ¿Necesito cambiar mi HIS?
    - ¿Cada cuánto se actualiza?
    - ¿Dónde están mis datos?
    - ¿Qué pasa si supero el cupo de IA?
    - ¿Funciona con un HIS distinto de SaludIPS?
12. **Llamado final:** «Agende un diagnóstico de 30 minutos con sus propios datos» y el formulario corto.
13. **Pie:** contacto, enlaces legales, redes y aviso de privacidad.

## 5. Página `/funcionalidades`
Cada módulo del punto 4.4 lleva:
- las **decisiones que habilita**, por ejemplo: «Renegociar con la EPS X», «Mover horas de dermatología a medicina interna», «Revisar el precio del procedimiento Y»;
- los **KPI que muestra**, tomados del catálogo `09_catalogo_kpi_gerencial.md`, en lenguaje de gerente y sin nombres técnicos;
- una captura o GIF con datos ficticios;
- el **perfil de IPS** al que aplica.

## 6. Página `/asistente-ia`
- Ejemplos de preguntas y respuestas con fuentes visibles.
- Qué datos usa y cuáles no: nunca datos de pacientes, y los nombres de personas se envían con seudónimo.
- Control de consumo: cupo incluido en el plan, consumo adicional opcional con tope, y tarifa visible.
- Limitaciones honestas: el análisis es automatizado y las cifras se validan antes de decisiones contables o tributarias.

## 7. Página `/planes`
Una tabla de planes (nombres y precios por definir) con:
- número de empresas y usuarios;
- módulos;
- **cupo de IA incluido**;
- tarifa del consumo adicional;
- frecuencia de actualización;
- soporte e implementación.

Botón «Hablar con ventas». **No publicar precios hasta definir el modelo de negocio.** Mientras tanto, usar «Desde $… / mes» o «Cotizar».

## 8. Formulario de demo
Campos: nombre, cargo, IPS, NIT, ciudad, número de sedes, HIS que usa (SaludIPS u otro), correo, teléfono, casilla de autorización de tratamiento de datos (obligatoria, con enlace a la política) y casilla de comunicaciones comerciales (opcional).
Al enviar: correo al equipo comercial y registro en el CRM (por definir) o en la BD de la plataforma (`crm.Prospecto`), más un mensaje de confirmación con agendamiento.

## 9. Requisitos no funcionales
- **Separada de la aplicación:** sitio estático o Blazor con render estático del lado del servidor (SSR) en .NET 10, hospedado en **SmarterASP** (D06). La aplicación vive en `app.[dominio]` (Azure).
- **SEO:** HTML pre-renderizado, títulos y descripciones por página, Open Graph, `sitemap.xml`, datos estructurados (`SoftwareApplication`, `FAQPage`) y la sección de recursos con contenido propio.
- **Rendimiento:** LCP < 2,5 s en móvil, imágenes en WebP o AVIF con carga diferida, sin librerías de gráficos pesadas (las capturas son imágenes).
- **Accesibilidad:** WCAG 2.2 AA, contraste, navegación por teclado y texto alternativo.
- **Analítica:** medición de conversiones (clic en demo y envío del formulario) **con banner de consentimiento de cookies**.
- **Formulario:** protección anti-spam (captcha o *honeypot*) y validación del lado del servidor.
- **Diseño:** formal y sobrio para el sector salud y la gerencia. Se reutiliza el sistema de diseño del portal: tipografía, paleta y componentes de tarjetas y KPI.
- **Idioma:** español (Colombia). Inglés no es prioridad.

## 10. Contenido que debe producirse
1. Nombre, logo y dominio.
2. Video de 90 s y capturas con una **empresa ficticia** completa (nunca datos de un cliente).
3. Textos de cada funcionalidad: los redacta producto y los valida un gerente de IPS.
4. Política de privacidad y términos del sitio, revisados por un abogado.
5. Primeros 4 artículos de recursos: glosas, conciliación de cartera con la Circular 030, facturas sin CUV y costeo en IPS.
6. Casos de éxito, solo después de los pilotos y con autorización.

## 11. Decisiones tomadas
1. **Nombre:** IAnalitica.
2. **Precios:** no se publican. `/planes` muestra los planes con sus características y el botón **«Cotizar»**, sin valores.
3. **Posicionamiento:** IAnalitica se presenta **independiente del HIS** («funciona con su HIS actual mediante un conector»).
4. **Caso de éxito: SaludIPS.** Va en una sección «Casos de éxito» del inicio y en la página `/casos/saludips`, como HIS integrado de forma nativa.
   - **Regla:** mientras no haya resultados medidos, el caso describe la integración (qué se conecta, en cuánto tiempo, qué tableros quedan activos). Cifras de mejora solo cuando un piloto las mida y la IPS autorice publicarlas.
   - Si el caso nombra una IPS cliente de SaludIPS, se necesita su autorización escrita.

## 12. Pendientes
1. Dominio e identidad visual (logo, paleta).
2. CRM para los prospectos.
