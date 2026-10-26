# BORRADOR — Autorización para el tratamiento de información mediante proveedor externo de inteligencia artificial

> **Documento base, sin validez hasta su revisión por el área jurídica.** Su función es dejar listo el instrumento para el caso de habilitar un proveedor que procese información fuera de Colombia, en particular en jurisdicciones sin un nivel adecuado de protección de datos reconocido. Debe ajustarse al contrato marco con cada IPS y a la normativa vigente (Ley 1581 de 2012, sus decretos reglamentarios y las instrucciones de la Superintendencia de Industria y Comercio sobre transferencia y transmisión internacional de datos). Versión: `IA-CONS-1.0-BORRADOR`.

---

**ENTRE:**
- **[RAZÓN SOCIAL DE LA IPS]**, NIT **[NIT]**, representada por **[NOMBRE]**, identificado con **[TIPO Y NÚMERO]**, en calidad de **[CARGO]** (en adelante, **«la Empresa»**), y
- **[RAZÓN SOCIAL DE LA PLATAFORMA]**, NIT **[NIT]** (en adelante, **«la Plataforma»**).

## 1. Objeto
La Empresa autoriza a la Plataforma a enviar a **[NOMBRE DEL PROVEEDOR DE IA]** (en adelante, «el Proveedor»), con procesamiento en **[PAÍS]**, la información estrictamente necesaria para prestar el servicio de **asistente de análisis financiero** del portal de dashboards, en los términos de este documento.

## 2. Información que se envía
2.1. **Sí se envía:** cifras financieras **agregadas** de la Empresa (ingresos, costos, gastos, cartera, flujo de caja, presupuesto, inventario) por periodo, sede, centro de costo, línea de servicio y pagador; nombres de entidades pagadoras y proveedores **personas jurídicas**; y el texto de las preguntas que formulen los usuarios.
2.2. **No se envía:**
- (a) datos de pacientes, ni identificados ni identificables;
- (b) historia clínica ni ningún dato de salud;
- (c) documentos de identidad;
- (d) nombres de personas naturales. Profesionales, empleados y usuarios se sustituyen por seudónimos antes del envío.
2.3. El usuario es responsable de no escribir en sus preguntas datos personales de terceros. La Plataforma muestra esta advertencia en el panel del asistente.

## 3. Finalidad
Generar análisis, explicaciones y respuestas a las preguntas de los usuarios autorizados de la Empresa sobre su propia información financiera. El Proveedor no podrá usar la información para fines propios, incluido el entrenamiento de modelos, **[conforme a sus condiciones comerciales, que se anexan]**.

## 4. Proveedor, ubicación y condiciones
- **Proveedor:** [razón social, domicilio].
- **País de procesamiento:** [país].
- **Retención de datos por el Proveedor:** [días / política].
- **Uso para entrenamiento:** [sí/no, según contrato].
- **Medidas de seguridad declaradas:** [cifrado en tránsito y reposo, certificaciones].
- **Anexo:** términos del Proveedor vigentes a la fecha de firma.

## 5. Declaración de riesgos
La Empresa declara conocer que:
- (a) la información se procesará en un país que puede no ofrecer un nivel de protección equivalente al colombiano;
- (b) estará sujeta a la legislación de ese país, incluidas las facultades de acceso de sus autoridades;
- (c) la Plataforma no controla la infraestructura del Proveedor;
- (d) las respuestas del asistente son automatizadas, pueden contener errores y **no sustituyen** el criterio profesional contable, financiero, tributario o jurídico.

## 6. Obligaciones de la Plataforma
1. Enviar solo la información descrita en el numeral 2.1.
2. Aplicar seudonimización a personas naturales.
3. Cifrar la información en tránsito.
4. Mantener un registro auditable de cada consulta: fecha, usuario, información enviada en forma de metadatos, tokens y costo.
5. Suspender el envío dentro de **[24] horas** después de recibir la revocación.
6. Informar a la Empresa sobre incidentes de seguridad del Proveedor que la afecten en cuanto los conozca.

## 7. Obligaciones de la Empresa
1. Contar con las autorizaciones necesarias respecto de la información que comparte.
2. Definir los usuarios y roles autorizados.
3. Instruir a sus usuarios para no incluir datos personales en las preguntas.

## 8. Costos
El uso del asistente se cobra según el plan contratado: el cupo incluido en el canon mensual y, cuando la Empresa lo autorice, el consumo adicional a la tarifa vigente publicada en el portal.

## 9. Vigencia y revocación
Esta autorización rige desde su firma y mientras esté vigente el contrato. La Empresa puede **revocarla en cualquier momento** desde el portal o por comunicación escrita. La revocación deshabilita el uso de ese Proveedor y no afecta el uso de proveedores para los que no se requiera esta autorización.

## 10. Aceptación
Leído y aceptado en **[CIUDAD]**, el **[FECHA]**.

| La Empresa | La Plataforma |
|---|---|
| Nombre: | Nombre: |
| Cargo: Representante legal | Cargo: |
| Identificación: | Identificación: |
| Firma: | Firma: |

---
**Registro en la plataforma:** al firmarse, se carga el PDF en el portal y se crea el registro en `ia.Consentimiento` con la versión del documento, el hash SHA‑256 del PDF, el firmante y la fecha. Sin ese registro vigente, el sistema no permite usar el proveedor.

**Pendiente jurídico:**
1. Confirmar si el envío al Proveedor es *transmisión* (el Proveedor actúa como encargado) o *transferencia* internacional, y los requisitos aplicables a cada caso.
2. Determinar si se requiere declaración de conformidad o algún trámite ante la SIC.
3. Revisar la cláusula de no entrenamiento contra los términos reales de cada proveedor.
4. Armonizar el documento con el contrato marco y con la política de tratamiento de datos de la Plataforma.
