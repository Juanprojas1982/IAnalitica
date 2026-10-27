/* 1020 · Validador de precios máximos (spec 15 §2). Completa fact.HallazgoTarifario (0080) para el ciclo de vida del hallazgo
   igual que el auditor de configuración: llave de deduplicación, nombre legible, justificación y última detección. */
ALTER TABLE fact.HallazgoTarifario ADD
    ClaveEntidad    nvarchar(200) NOT NULL CONSTRAINT DF_HTar_Clave DEFAULT N'',     -- «CodSuministro|IdEmpresa» (R1, R2, R5) o «CodSuministro» (R6)
    NombreEntidad   nvarchar(300) NULL,
    Detalle         nvarchar(400) NULL,
    Justificacion   nvarchar(500) NULL,
    UltimaDeteccion datetime2(0) NOT NULL CONSTRAINT DF_HTar_Ultima DEFAULT SYSUTCDATETIME();
GO
CREATE UNIQUE INDEX UX_HTar_Abierto ON fact.HallazgoTarifario (EmpresaId, Regla, ClaveEntidad) WHERE Estado IN ('NUEVO', 'ABIERTO');
GO
