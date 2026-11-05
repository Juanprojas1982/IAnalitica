/* 1010 · Auditor de configuración (spec 16): historial del índice de salud y semillas de las 10 reglas de la fase 1.
   Los textos son de fábrica y se pueden editar sin código (cfg.ReglaConfiguracion). RutaSaludIPS solo se llena donde la spec la da;
   el resto queda NULL hasta confirmarla con el usuario funcional. PorVerificar = 1: la regla descansa en un supuesto [S] de la spec 16. */
CREATE TABLE fact.IndiceSaludConfiguracion (
    EmpresaId int NOT NULL CONSTRAINT FK_ISC_Empresa REFERENCES seg.Empresa,
    Fecha     date NOT NULL,
    Area      varchar(20) NOT NULL,                    -- 'TOTAL' o el área de la regla
    Indice    tinyint NOT NULL CHECK (Indice BETWEEN 0 AND 100),
    Abiertos  int NOT NULL,
    CONSTRAINT PK_IndiceSaludConfiguracion PRIMARY KEY (EmpresaId, Fecha, Area)
);
GO
INSERT cfg.ReglaConfiguracion (Codigo, Area, SeveridadBase, Titulo, PorQueImporta, ComoCorregir, RutaSaludIPS, UmbralesJson, VersionMinimaSaludIPS, PorVerificar) VALUES
('CON-01', 'Contabilidad', 'CRITICA', N'Pagador activo sin cuentas contables en la vigencia',
 N'Las facturas del pagador no generan movimiento contable y el envío a contabilidad falla.',
 N'Registra las cuentas contables del pagador (cuenta por cobrar, ingresos, descuentos y glosas) para la vigencia actual.',
 N'Facturación › Empresas › Cuentas contables', NULL, NULL, 0),
('CON-02', 'Contabilidad', 'ALTA', N'Configuración del año siguiente incompleta',
 N'El 1 de enero dejará de contabilizar todo lo que no tenga configuración de la nueva vigencia.',
 N'Copia o crea la configuración del año siguiente (cuentas de pagadores, comprobantes y parámetros anuales) antes de fin de año.',
 NULL, N'{"DesdeMes":11,"CriticaDesdeMes":12}', NULL, 1),
('CON-04', 'Contabilidad', 'ALTA', N'Cuenta por cobrar de pagadores sin detalle de cartera',
 N'La cartera de los pagadores que usan esta cuenta no aparece en los reportes de cartera ni en IAnalitica.',
 N'Marca la cuenta con detalle de cartera «por cobrar» (DetalleCartera = 2) en el plan de cuentas de la vigencia.',
 NULL, NULL, NULL, 0),
('FE-01', 'Factura electrónica', 'ALTA', N'Resolución DIAN vencida o por vencer',
 N'Con la resolución vencida no se pueden emitir facturas electrónicas válidas.',
 N'Tramita la nueva resolución de numeración ante la DIAN y regístrala en SaludIPS antes del vencimiento.',
 NULL, N'{"DiasAviso":30}', NULL, 1),
('FE-02', 'Factura electrónica', 'ALTA', N'Numeración de la resolución por agotarse',
 N'Al agotarse el rango autorizado se detiene la facturación.',
 N'Solicita a la DIAN un nuevo rango de numeración y regístralo en SaludIPS.',
 NULL, N'{"PorcentajeAviso":90}', NULL, 1),
('FE-03', 'Factura electrónica', 'CRITICA', N'Sede que factura sin resolución DIAN asignada',
 N'Las facturas de esta sede no tienen una resolución vigente asociada y pueden ser rechazadas.',
 N'Asigna una resolución DIAN vigente a la sede y a sus tipos de comprobante.',
 NULL, NULL, NULL, 1),
('TAR-01', 'Tarifas', 'CRITICA', N'Pagador activo sin manual tarifario',
 N'Los servicios del pagador se facturan en cero o con tarifa manual.',
 N'Asigna el manual tarifario al pagador.',
 NULL, NULL, NULL, 1),
('TAR-02', 'Tarifas', 'ALTA', N'Procedimientos del portafolio sin tarifa para el pagador',
 N'Los procedimientos que el pagador puede usar y no tienen tarifa en su manual salen sin valor.',
 N'Carga la tarifa de esos procedimientos en el manual del pagador (reporte SIPS_198).',
 NULL, NULL, NULL, 1),
('TAR-06', 'Tarifas', 'ALTA', N'Procedimientos sin CUPS o suministros sin CUM',
 N'Sin CUPS o CUM los RIPS y la factura electrónica asociados son rechazados.',
 N'Completa el código CUPS de los procedimientos y el CUM de los suministros.',
 NULL, NULL, NULL, 0),
('USU-01', 'Seguridad', 'CRITICA', N'Usuario activo de un empleado retirado',
 N'Un usuario sin vínculo laboral vigente conserva acceso al sistema.',
 N'Bloquea o inactiva el usuario del empleado retirado.',
 NULL, NULL, NULL, 1);
GO
