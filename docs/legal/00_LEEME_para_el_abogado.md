# Paquete legal IAnalitica — instrucciones para el abogado revisor

> Estos documentos son **borradores técnicos** preparados con asistencia de IA para acelerar el trabajo. **No son asesoría legal** y no tienen validez hasta que un abogado en Colombia los revise, ajuste y avale. Las normas citadas deben verificarse en su versión vigente.

## Contexto del negocio (para el revisor)
- **IAnalitica** es una plataforma en la nube (SaaS) de analítica financiera y operativa para instituciones de salud: IPS privadas, ESE públicas y operadores farmacéuticos.
- Recibe cada noche datos de los sistemas de cada institución (principalmente el HIS SaludIPS) mediante un conector instalado en sus servidores. Con eso genera tableros, alertas, auditoría de configuración, validación de precios de medicamentos y un asistente de IA.
- **Datos que trata:**
  - información financiera y operativa de la institución;
  - datos de terceros que son personas jurídicas (EPS, proveedores);
  - datos de personas naturales vinculadas a la institución (profesionales, usuarios del sistema, empleados, con costos de nómina agregados);
  - datos de pacientes **solo seudonimizados mediante un hash irreversible calculado en el servidor de la institución**, sin nombres, documentos ni datos clínicos.
- **Rol frente a la Ley 1581 de 2012:**
  - IAnalitica es **encargada** del tratamiento de los datos que le entrega cada institución, que es la **responsable**;
  - IAnalitica es **responsable** de sus propios datos: usuarios del portal, prospectos de la web, contactos comerciales.
- **Proveedores (subencargados):**
  - nube (Azure, región por definir, posiblemente fuera de Colombia);
  - IA (Anthropic, EE. UU.), solo con datos agregados y nombres de personas seudonimizados;
  - correo transaccional.
- **Clientes:** privados y **públicos (ESE)**. Para las ESE puede aplicar el régimen de contratación estatal o el régimen especial de la ESE.

## Documentos
| # | Documento | Uso |
|---|---|---|
| 01 | Contrato de prestación de servicios SaaS, con anexos A (encargo del tratamiento de datos), B (niveles de servicio) y C (asistente de IA) | Con cada institución |
| 02 | Política de tratamiento de datos personales de IAnalitica | Publicada en la web y la plataforma |
| 03 | Términos y condiciones de uso de la plataforma | Aceptación de cada usuario al ingresar |
| 04 | Aviso de privacidad, autorización del formulario y política de cookies (sitio web) | Landing |
| 05 | Acuerdo de piloto, con confidencialidad y autorización de caso de éxito | Las 3 instituciones piloto |
| — | Consentimiento para proveedor de IA con procesamiento en otros países | Ya preparado en `../08_consentimiento_proveedor_ia.md`, solo si se habilita un proveedor distinto de Anthropic |

## Puntos que requieren criterio jurídico
1. Si el envío a los proveedores de nube e IA en el exterior es **transmisión** (encargado) o **transferencia** internacional, y qué requisitos aplican (país de destino, contrato de transmisión, declaraciones ante la SIC si proceden).
2. Si el **hash de pacientes** calculado en origen alcanza para tratar esa información como no identificable frente a IAnalitica, y qué salvaguardas adicionales recomienda.
3. La obligación de **registro en el RNBD** de IAnalitica por sus propias bases (usuarios, prospectos), según su tamaño y naturaleza.
4. Modalidad de contratación con las **ESE** (régimen especial, manual de contratación de cada ESE) y las cláusulas exorbitantes o garantías que podrían exigir.
5. Límite de responsabilidad y su validez frente a cada tipo de cliente.
6. El **validador de precios máximos** y el **auditor de configuración**: dejar claro que son herramientas de apoyo y que la decisión y la corrección son de la institución.
7. Tratamiento tributario del cobro por excedente de IA (facturación electrónica del servicio).
8. Propiedad intelectual del software (Ley 23 de 1982, Decisión Andina 351 de 1993) y licencia de uso.
9. Firma electrónica de los contratos (Ley 527 de 1999 y su reglamentación).

## Datos a completar en todos los documentos
`[RAZÓN SOCIAL TITULAR]`, `[NIT]`, `[DOMICILIO]`, `[REPRESENTANTE LEGAL]`, `[CORREO DE PRIVACIDAD]`, `[DOMINIO]`, `[CIUDAD]`, `[REGIÓN DE NUBE]`.
