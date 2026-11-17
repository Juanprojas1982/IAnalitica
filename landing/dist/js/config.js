// Configuración del sitio. Al publicar, completar:
//  - endpointProspectos: URL de POST /v1/prospectos de la plataforma (ver docs/24 §5.9). Vacío = el formulario abre el correo comercial; sin correo real, avisa que no está habilitado.
//  - correoComercial: buzón que recibe las solicitudes mientras no exista el endpoint. Vacío o con el valor de fábrica = no se muestra en el pie.
//  - urlPortal: dirección de la aplicación (botón «Ingresar»). Vacío, con el valor de fábrica o empezando por # = no se muestra Ingresar.
window.IANALITICA = { endpointProspectos: "", correoComercial: "comercial@dominio-por-definir.co", urlPortal: "#portal-por-definir" };
