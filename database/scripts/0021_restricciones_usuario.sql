/* 0021 · Restricciones de negocio sobre la tabla de Identity seg.Usuario (D27). */
ALTER TABLE seg.Usuario ADD CONSTRAINT CK_Usuario_Empresa
    CHECK ((EsSuperAdmin = 1 AND EmpresaId IS NULL) OR (EsSuperAdmin = 0 AND EmpresaId IS NOT NULL));
ALTER TABLE seg.Usuario ADD CONSTRAINT FK_Usuario_Empresa FOREIGN KEY (EmpresaId) REFERENCES seg.Empresa(EmpresaId);
CREATE UNIQUE INDEX UX_Usuario_AdminPrincipal ON seg.Usuario(EmpresaId) WHERE EsAdminPrincipal = 1;   -- D27
