/* 1000 · Copia por empresa de la configuración de SaludIPS (spec 16 §2). GENERADO por database/generar-saludips.py: no editar a mano.
   Una tabla por cada tabla de la lista blanca del conector. EmpresaId y CargaId se agregan; la RLS (siempre/9000) las cubre sola. */
CREATE TABLE saludips.dbo_mEmpresas (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_mEmpresas_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [IdEmpresa] int NOT NULL,
    [NomEmpresa] varchar(255) NULL,
    [CodManual] smallint NULL,
    [DatosRemision] smallint NULL,
    [ValorPactado] smallint NULL,
    [CodPlan] smallint NULL,
    [IdContrato] int NULL,
    [CodInstitucionAdm] varchar(12) NULL,
    [IdEmpresaExcedente] int NULL,
    [CodContable] varchar(20) NULL,
    [CentroCostoC] varchar(4) NULL,
    [SubCentroCostoC] varchar(3) NULL,
    [IdcUsuario] int NULL,
    [FechaDigitacion] smalldatetime NULL,
    [CodEstado] smallint NULL,
    [CodContableIng] varchar(50) NULL,
    [Vigenciafactura] smallint NULL,
    [CodContableIngDev] varchar(20) NULL,
    [CodContableRadicada] varchar(20) NULL,
    [CodContableGlosaIDeb] varchar(20) NULL,
    [CodContableGlosaICre] varchar(20) NULL,
    [CodContableGlosaPDeb] varchar(20) NULL,
    [CodContableGlosaPCre] varchar(20) NULL,
    [CodContableDescuento] varchar(20) NULL,
    [UsuarioID] int NULL,
    [EstadoEntidad] tinyint NULL,
    [ManejaContabilidadFactura] smallint NULL,
    CONSTRAINT PK_dbo_mEmpresas PRIMARY KEY (EmpresaId, [IdEmpresa])
);
GO
CREATE TABLE saludips.dbo_mEmpresasCuentasContables (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_mEmpresasCuentasContables_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [IdEmpresa] int NULL,
    [Vigencia] int NULL,
    [CuentaPorCobrarId] int NULL,
    [CuentaPorCobrarRadicadaId] int NULL,
    [CuentaIngresosId] int NULL,
    [CuentaIngresosDevolucionId] int NULL,
    [CuentaDescuentoId] int NULL,
    [CuentaGlosaInicialDebitoId] int NULL,
    [CuentaGlosaInicialCreditoId] int NULL,
    [CuentaTramiteGlosaDebitoId] int NULL,
    [CuentaTramiteGlosaCreditoId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    [CuentaPorCobrarRadicadaVigenciaAnteriorId] int NULL,
    CONSTRAINT PK_dbo_mEmpresasCuentasContables PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.dbo_mContratos (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_mContratos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [IdContrato] int NOT NULL,
    [CodInstitucionAdm] varchar(12) NULL,
    [NoContrato] varchar(20) NULL,
    [FechaContrato] smalldatetime NULL,
    [FechaFinaliza] smalldatetime NULL,
    [DuracionContrato] smallint NULL,
    [CodEstadoContrato] smallint NULL,
    [CodTipoUsuario] smallint NULL,
    [NoUsuarios] int NULL,
    [VrPorUsuario] float NULL,
    [VrContrato] float NULL,
    [PacientesAtendidos] int NULL,
    [VrPacientesAtendidos] float NULL,
    [PacientesFacturados] int NULL,
    [VrPacientesFacturados] float NULL,
    [ConBase] smallint NULL,
    [Servicios] varchar(255) NULL,
    [Soportes] varchar(255) NULL,
    [CodTipoContrato] int NULL,
    [IdSede] varchar(255) NULL,
    [CodTipoCartera] int NULL,
    [IdcUsuario] int NULL,
    [UsuarioID] int NULL,
    [EstadoEntidad] tinyint NULL,
    [FechaDigitacion] smalldatetime NULL,
    [CodPresupuestal] varchar(50) NULL,
    [Detalles] varchar(200) NULL,
    [CoberturaPlan] int NULL,
    [BloquearTopeContrato] bit NULL,
    [DiasAnulacionFactura] int NULL,
    [VrInicial] float NULL,
    [InterDependencia] smallint NULL,
    CONSTRAINT PK_dbo_mContratos PRIMARY KEY (EmpresaId, [IdContrato])
);
GO
CREATE TABLE saludips.dbo_SedesXContrato (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_SedesXContrato_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [IdContrato] int NOT NULL,
    [IdSede] int NOT NULL,
    CONSTRAINT PK_dbo_SedesXContrato PRIMARY KEY (EmpresaId, [IdContrato], [IdSede])
);
GO
CREATE TABLE saludips.dbo_TipoServiciosXEmpresa (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_TipoServiciosXEmpresa_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [IdEmpresa] int NOT NULL,
    [CodTipoServicio] smallint NOT NULL,
    CONSTRAINT PK_dbo_TipoServiciosXEmpresa PRIMARY KEY (EmpresaId, [IdEmpresa], [CodTipoServicio])
);
GO
CREATE TABLE saludips.dbo_pServiciosEmpresa (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pServiciosEmpresa_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [IdEmpresa] int NOT NULL,
    [CodTipoServicio] smallint NOT NULL,
    [Descuento] float NULL,
    CONSTRAINT PK_dbo_pServiciosEmpresa PRIMARY KEY (EmpresaId, [IdEmpresa], [CodTipoServicio])
);
GO
CREATE TABLE saludips.dbo_mInstituciones (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_mInstituciones_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [CodInstitucion] varchar(12) NOT NULL,
    [CodClaseInstitucion] varchar(2) NULL,
    [NitInstitucion] varchar(30) NULL,
    [NomInstitucion] varchar(100) NULL,
    [CodCiudad] varchar(5) NULL,
    [Direccion] varchar(100) NULL,
    [Telefono] varchar(30) NULL,
    [Fax] varchar(20) NULL,
    [RepresentanteLegal] varchar(35) NULL,
    [Contacto] varchar(255) NULL,
    [Contrata] smallint NULL,
    [IdcUsuario] int NULL,
    [FechaDigitacion] smalldatetime NULL,
    [ResponsableGlosas] smallint NULL,
    [TerceroId] int NULL,
    CONSTRAINT PK_dbo_mInstituciones PRIMARY KEY (EmpresaId, [CodInstitucion])
);
GO
CREATE TABLE saludips.dbo_pEstratosCopago (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pEstratosCopago_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [CodEstrato] int NOT NULL,
    [Porcentaje] float NULL,
    [Cuota] float NULL,
    [MaxEvento] float NULL,
    [MaxAnual] float NULL,
    CONSTRAINT PK_dbo_pEstratosCopago PRIMARY KEY (EmpresaId, [CodEstrato])
);
GO
CREATE TABLE saludips.dbo_pTarifasProcedimientos (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pTarifasProcedimientos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [CodProcedimiento] varchar(10) NOT NULL,
    [CodManual] smallint NOT NULL,
    [CodAlterno] varchar(20) NULL,
    [Valor] float NULL,
    [GrupoQx] float NULL,
    [Puntos] float NULL,
    [CodPlan] smallint NULL,
    [ValorAnterior] float NULL,
    [IdcUsuario] int NULL,
    [FechaDigitacion] smalldatetime NULL,
    [CodIva] varchar(3) NULL,
    [Comentario] varchar(max) NULL,
    CONSTRAINT PK_dbo_pTarifasProcedimientos PRIMARY KEY (EmpresaId, [CodProcedimiento], [CodManual])
);
GO
CREATE TABLE saludips.dbo_pTarifasSuministros (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pTarifasSuministros_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [CodSuministro] varchar(30) NOT NULL,
    [CodManual] smallint NOT NULL,
    [CodAlterno] varchar(20) NULL,
    [Valor] float NULL,
    [CodPlan] smallint NULL,
    [ValorAnterior] float NULL,
    [IdcUsuario] int NULL,
    [FechaDigitacion] smalldatetime NULL,
    [CodIva] varchar(3) NULL,
    [ManejaIva] bit NULL,
    [Comentario] varchar(max) NULL,
    CONSTRAINT PK_dbo_pTarifasSuministros PRIMARY KEY (EmpresaId, [CodSuministro], [CodManual])
);
GO
CREATE TABLE saludips.dbo_pPortafolio (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pPortafolio_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [CodProcedimiento] varchar(10) NOT NULL,
    [CodTipoServicio] int NOT NULL,
    [CodCentroCosto] varchar(10) NULL,
    CONSTRAINT PK_dbo_pPortafolio PRIMARY KEY (EmpresaId, [CodProcedimiento], [CodTipoServicio])
);
GO
CREATE TABLE saludips.dbo_pProcedimientos (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pProcedimientos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [CodProcedimiento] varchar(10) NOT NULL,
    [NomProcedimiento] varchar(255) NULL,
    [CodRips] varchar(2) NULL,
    [Frecuente] smallint NULL,
    [ExigeNacimiento] smallint NULL,
    [CodPaquete] varchar(10) NULL,
    [CodCentroCosto] int NULL,
    [CodNivel] smallint NULL,
    [CodContable] varchar(20) NULL,
    [Laboratorio] smallint NULL,
    [CodActividadBase] int NULL,
    [CodFinalidadProcedimiento] int NULL,
    [codEvalPlan] varchar(20) NULL,
    [CodTipoProcedimiento] int NULL,
    [IdcUsuario] int NULL,
    [FechaDigitacion] smalldatetime NULL,
    [CodSoat] varchar(10) NULL,
    [NomSoat] varchar(255) NULL,
    [PrimeraVez] smallint NULL,
    [CodEspecialidad] smallint NULL,
    [RequiereActoQx] smallint NULL,
    [EsTipoOdontologia] bit NULL,
    [PatologiaMayor] smallint NULL,
    [ArticuloGenericoId] int NULL,
    [RequiereDx] smallint NULL,
    [CodServicioHab] varchar(4) NULL,
    [CodCups] varchar(20) NULL,
    CONSTRAINT PK_dbo_pProcedimientos PRIMARY KEY (EmpresaId, [CodProcedimiento])
);
GO
CREATE TABLE saludips.dbo_pSuministros (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pSuministros_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [CodSuministro] varchar(30) NOT NULL,
    [NomSuministro] varchar(255) NULL,
    [CodGenerico] int NULL,
    [CodRips] varchar(2) NULL,
    [Frecuente] smallint NULL,
    [CodConcentracion] int NULL,
    [CodUnidadConsumo] int NULL,
    [CodTipoMedicamento] int NULL,
    [CodPresentacion] int NULL,
    [CodContable] varchar(20) NULL,
    [Existencias] int NULL,
    [CodUnidadCompra] int NULL,
    [IdcUsuario] int NULL,
    [FechaDigitacion] smalldatetime NULL,
    [CodTipoControl] smallint NULL,
    [CodCum] varchar(20) NULL,
    [OrdenaEnfermeriaAM] bit NULL,
    [Costo] float NULL,
    [StockMinimo] int NULL,
    [StockMaximo] int NULL,
    [InactivarHC] bit NULL,
    [TotalizarCero] bit NULL,
    [CodViaAdministracion] int NULL,
    [RequiereHorario] bit NULL,
    [CodDiluyente] smallint NULL,
    [CodTiempoInfusion] smallint NULL,
    [CodVolumen] int NULL,
    [CodigoATC] varchar(15) NULL,
    [UnidadMedida] float NULL,
    [CodRelacionadoAlmacen] varchar(200) NULL,
    [Consecutivo] int NULL,
    [HorarioVariable] smallint NULL,
    [ArticuloGenericoId] int NULL,
    [RequiereDx] smallint NULL,
    CONSTRAINT PK_dbo_pSuministros PRIMARY KEY (EmpresaId, [CodSuministro])
);
GO
CREATE TABLE saludips.dbo_pTipoServicio (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pTipoServicio_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [CodTipoServicio] smallint NOT NULL,
    [NomTipoServicio] varchar(30) NULL,
    [CodClaseServicio] smallint NULL,
    [NoOrden] int NULL,
    [CodCentroCosto] smallint NULL,
    [CodGrupoServicio] smallint NULL,
    [CodCuenta] varchar(20) NULL,
    [CentroCostoC] varchar(4) NULL,
    [SubCentroCostoC] varchar(3) NULL,
    [IdcUsuario] int NULL,
    [FechaDigitacion] smalldatetime NULL,
    [CodCuentaDev] varchar(20) NULL,
    [IncruentoQx] smallint NULL,
    [FiltraOrden] varchar(20) NULL,
    CONSTRAINT PK_dbo_pTipoServicio PRIMARY KEY (EmpresaId, [CodTipoServicio])
);
GO
CREATE TABLE saludips.dbo_pTiposConceptosCuentasContables (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pTiposConceptosCuentasContables_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [CodTipoConcepto] int NULL,
    [Vigencia] int NULL,
    [CuentaId] int NULL,
    [CuentaDevolucionId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_dbo_pTiposConceptosCuentasContables PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_Cuentas (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_Cuentas_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [NormatividadId] int NULL,
    [Vigencia] smallint NULL,
    [CuentaPadreId] int NULL,
    [SedeId] int NULL,
    [Codigo] varchar(15) NULL,
    [Nombre] varchar(250) NULL,
    [Nivel] smallint NULL,
    [Naturaleza] tinyint NULL,
    [TipoInformacion] tinyint NULL,
    [SaldoInicial] float NULL,
    [TipoCuenta] tinyint NULL,
    [ManejaMovimientos] bit NULL,
    [MovimientoContrario] bit NULL,
    [ManejaTerceros] bit NULL,
    [ManejaCentroCosto] bit NULL,
    [Corriente] bit NULL,
    [Nomina] bit NULL,
    [UsoCuenta] tinyint NULL,
    [Anticipo] bit NULL,
    [CierreCosto] bit NULL,
    [CuentaCierreCostosId] int NULL,
    [DisplayOrder] float NULL,
    [CuentaNIIFId] int NULL,
    [RubroFuenteId] int NULL,
    [DetalleCartera] tinyint NULL,
    [Dian1001Id] int NULL,
    [Formato206Id] int NULL,
    [Formato207Id] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    [Cuenta_Id] int NULL,
    [ManejaDocumentoCruce] bit NULL,
    [TipoProcesoCuentaPorCobrar] tinyint NULL,
    [Dian1001CGDeducible] bit NULL,
    [Dian1007Id] int NULL,
    [Dian1007TipoCuenta] tinyint NULL,
    [Dian1008Id] int NULL,
    [Dian1009Id] int NULL,
    [Dian1012Id] int NULL,
    [Dian2276Id] int NULL,
    [ExogenaMunicipal_GF_F_118Id] int NULL,
    [Dian1001TipoCuenta] tinyint NULL,
    CONSTRAINT PK_SIIFNet_Cuentas PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_CentrosCostos (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_CentrosCostos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [Vigencia] smallint NULL,
    [Codigo] varchar(15) NULL,
    [Nivel] smallint NULL,
    [Nombre] varchar(100) NULL,
    [NombreCorto] varchar(15) NULL,
    [ManejaMovimientos] bit NULL,
    [DisplayOrder] real NULL,
    [TipoCentroCosto] tinyint NULL,
    [ResponsableId] int NULL,
    [Direccion] varchar(100) NULL,
    [Telefono] varchar(100) NULL,
    [CentroCostoPadreId] int NULL,
    [CuentaIngresosCarteraId] int NULL,
    [DependenciaId] int NULL,
    [UnidadNegocioId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    [CuentaDevolucionIngresosCarteraId] int NULL,
    [CuentaDevolucionIngresosCarteraVigenciaAnteriorId] int NULL,
    [CuentaIngresosCopagoId] int NULL,
    [CuentaDevolucionIngresosCopagoId] int NULL,
    CONSTRAINT PK_SIIFNet_CentrosCostos PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_TiposComprobantes (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_TiposComprobantes_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [ModuloId] int NULL,
    [NormatividadId] int NULL,
    [Prefijo] varchar(4) NULL,
    [Nombre] varchar(60) NULL,
    [Descripcion] varchar(max) NULL,
    [Clase] tinyint NULL,
    [EstadoComprobante] tinyint NULL,
    [ClaseTipoComprobante] tinyint NULL,
    [EsPlantillaBaseMovimiento] bit NULL,
    [TipoComprobanteBaseId] int NULL,
    [EsMovimientoConfirmado] bit NULL,
    [PermiteIngresarValorUnitario] bit NULL,
    [TipoMovimientoAlmacen] tinyint NULL,
    [ClaseTerceroPermitido] smallint NULL,
    [ManejaCuentasContables] bit NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    [ManejaActasRecepcion] bit NULL,
    [ManejaDocumentoRelacionado] bit NULL,
    [TipoDocumentoDIAN] tinyint NULL,
    [FuncionalidadPresupuestal] tinyint NULL,
    [TipoComprobantePresupuestalId] int NULL,
    [TipoComprobante_Id] int NULL,
    [ContabilidadxTercero] bit NULL,
    CONSTRAINT PK_SIIFNet_TiposComprobantes PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_TiposComprobantesCuentasContables (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_TiposComprobantesCuentasContables_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [TipoComprobanteId] int NULL,
    [Vigencia] smallint NULL,
    [CuentaDebitoId] int NULL,
    [CuentaCreditoId] int NULL,
    [CuentaDescuentoId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_SIIFNet_TiposComprobantesCuentasContables PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_TiposComprobantesCentrosCostos (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_TiposComprobantesCentrosCostos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [TipoComprobanteId] int NULL,
    [Vigencia] smallint NULL,
    [CentroCostoId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_SIIFNet_TiposComprobantesCentrosCostos PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_FormasPagosCuentasContables (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_FormasPagosCuentasContables_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [FormaPagoId] int NULL,
    [Vigencia] smallint NULL,
    [CuentaId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_SIIFNet_FormasPagosCuentasContables PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_BancosCuentas (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_BancosCuentas_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [BancoId] int NULL,
    [CuentaId] int NULL,
    [NumCuentaBancaria] varchar(30) NULL,
    [BancoSIAId] int NULL,
    [FuenteFinanciacionSIAId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_SIIFNet_BancosCuentas PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_CierresContables (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_CierresContables_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [Vigencia] int NULL,
    [Mes] tinyint NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_SIIFNet_CierresContables PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_Retenciones (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_Retenciones_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [Vigencia] smallint NULL,
    [ClaseIngresoGasto] tinyint NULL,
    [Codigo] varchar(3) NULL,
    [Nombre] varchar(250) NULL,
    [Descripcion] varchar(255) NULL,
    [TipoRetencionId] int NULL,
    [CuentaId] int NULL,
    [Clase] tinyint NULL,
    [ValorCalculo] float NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    [BaseRetencion] float NULL,
    CONSTRAINT PK_SIIFNet_Retenciones PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Almacen_ArticulosBase (
    EmpresaId int NOT NULL CONSTRAINT FK_Almacen_ArticulosBase_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [Codigo] varchar(20) NULL,
    [Nombre] varchar(255) NULL,
    [Descripcion] varchar(255) NULL,
    [ClaseArticulo] smallint NULL,
    [ManejaLotes] bit NULL,
    [DetalladoPorLaboratorio] bit NULL,
    [SubGrupoId] int NULL,
    [CodigoRapido] varchar(30) NULL,
    [CodigoBarras] varchar(60) NULL,
    [CodigosBarrasAdyacentes] varchar(500) NULL,
    [MarcaId] int NULL,
    [UnidadMedidaId] int NULL,
    [StockMinimo] int NULL,
    [StockMaximo] int NULL,
    [PorcentajeToleranciaVlrCompra] float NULL,
    [ReferenciaFabrica] varchar(60) NULL,
    [ValorUltimaCompra] float NULL,
    [Tarifa] tinyint NULL,
    [IVAId] int NULL,
    [FechaModificacion] datetime NULL,
    [ArticuloGenericoId] int NULL,
    [NombreComercial] varchar(255) NULL,
    [FormaFarmaceuticaId] int NULL,
    [GrupoEspecialId] int NULL,
    [LaboratorioId] int NULL,
    [PresentacionComercialId] int NULL,
    [PrincipioActivoId] int NULL,
    [ConcentracionId] int NULL,
    [POS] bit NULL,
    [CadenaFrio] bit NULL,
    [MedicamentoRegulado] bit NULL,
    [MedicamentoControlado] bit NULL,
    [CodigoATC] varchar(30) NULL,
    [CodigoCUM] varchar(30) NULL,
    [CodigoIUM] nvarchar(60) NULL,
    [ReportaSISMED] bit NULL,
    [IncluyeIva] bit NULL,
    [ClasificacionRiesgoId] int NULL,
    [UsoId] int NULL,
    [Serie] varchar(max) NULL,
    [VidaUtil] int NULL,
    [IdentificadorBusqueda] varchar(max) NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    [ManejaSolicitudesInternas] bit NULL,
    [GrupoFarmacologicoId] int NULL,
    CONSTRAINT PK_Almacen_ArticulosBase PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Almacen_Lotes (
    EmpresaId int NOT NULL CONSTRAINT FK_Almacen_Lotes_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [ArticuloId] int NULL,
    [NumeroLote] varchar(60) NULL,
    [FechaVencimiento] datetime NULL,
    [RegistroInvimaId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_Almacen_Lotes PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Almacen_RegistrosInvima (
    EmpresaId int NOT NULL CONSTRAINT FK_Almacen_RegistrosInvima_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [ArticuloId] int NULL,
    [RegistroSanitario] varchar(60) NULL,
    [FechaExpedicion] datetime NULL,
    [FechaVigencia] datetime NULL,
    [ExpedienteRegistroInvima] varchar(max) NULL,
    [VigenciaRegistroInvimaId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_Almacen_RegistrosInvima PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Almacen_Bodegas (
    EmpresaId int NOT NULL CONSTRAINT FK_Almacen_Bodegas_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [Codigo] varchar(10) NULL,
    [Nombre] varchar(255) NULL,
    [ResponsableId] int NULL,
    [Descripcion] varchar(255) NULL,
    [Direccion] varchar(255) NULL,
    [Telefono] varchar(255) NULL,
    [codBodegaHomologada] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    [ManejaSolicitudesInternas] bit NULL,
    CONSTRAINT PK_Almacen_Bodegas PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Almacen_ConfiguracionesContablesArticulos (
    EmpresaId int NOT NULL CONSTRAINT FK_Almacen_ConfiguracionesContablesArticulos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [ArticuloBaseId] int NULL,
    [Vigencia] smallint NULL,
    [CuentaInventarioId] int NULL,
    [CuentaCostosId] int NULL,
    [CuentaVentaId] int NULL,
    [CuentaDevolucionComprasId] int NULL,
    [CuentaDevolucionVentasId] int NULL,
    [CuentaIngresoAjusteId] int NULL,
    [CuentaPerdidaAjusteId] int NULL,
    [CuentaIvaComprasId] int NULL,
    [CuentaIvaVentasId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_Almacen_ConfiguracionesContablesArticulos PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Almacen_ConfiguracionesContablesGrupos (
    EmpresaId int NOT NULL CONSTRAINT FK_Almacen_ConfiguracionesContablesGrupos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [GrupoId] int NULL,
    [Vigencia] smallint NULL,
    [CuentaInventarioId] int NULL,
    [CuentaCostosId] int NULL,
    [CuentaVentaId] int NULL,
    [CuentaDevolucionComprasId] int NULL,
    [CuentaDevolucionVentasId] int NULL,
    [CuentaIngresoAjusteId] int NULL,
    [CuentaPerdidaAjusteId] int NULL,
    [CuentaIvaComprasId] int NULL,
    [CuentaIvaVentasId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_Almacen_ConfiguracionesContablesGrupos PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Almacen_ConfiguracionesContablesGruposCentrosCostos (
    EmpresaId int NOT NULL CONSTRAINT FK_Almacen_ConfiguracionesContablesGruposCentrosCostos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [GrupoId] int NULL,
    [CentroCostoId] int NULL,
    [Vigencia] smallint NULL,
    [CuentaCostosId] int NULL,
    [CuentaDevolucionId] int NULL,
    [CuentaDepreciacionId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_Almacen_ConfiguracionesContablesGruposCentrosCostos PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Nomina_CuentasCentrosCostos (
    EmpresaId int NOT NULL CONSTRAINT FK_Nomina_CuentasCentrosCostos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [CentroCostoId] int NULL,
    [ClaseConceptoId] int NULL,
    [CuentaId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    [Vigencia] smallint NULL,
    CONSTRAINT PK_Nomina_CuentasCentrosCostos PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Nomina_ConfiguracionesAnuales (
    EmpresaId int NOT NULL CONSTRAINT FK_Nomina_ConfiguracionesAnuales_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [SalarioMinimoLegal] float NULL,
    [AuxilioTransporte] float NULL,
    [SMLVFondoSolidaridad] int NULL,
    [PorcentajeCajaCompensacionFamiliar] float NULL,
    [PorcentajeICBF] float NULL,
    [PorcentajeSENA] float NULL,
    [UVT] float NULL,
    [Redondeo100SaludPension] smallint NULL,
    [NumeroDecimales] tinyint NULL,
    [ExcentoPagoParafiscales] smallint NULL,
    [MultiplesContratosXEmpleado] smallint NULL,
    [MultiplesVacacionesXControl] smallint NULL,
    [InterfazContable] tinyint NULL,
    [CrearTerceroContable] smallint NULL,
    [ResLiquidacionPrestaciones] tinyint NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    [Vigencia] smallint NULL,
    [SubsidioAlimentacion] float NULL,
    CONSTRAINT PK_Nomina_ConfiguracionesAnuales PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Nomina_Conceptos (
    EmpresaId int NOT NULL CONSTRAINT FK_Nomina_Conceptos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [Codigo] varchar(30) NULL,
    [Nombre] varchar(255) NULL,
    [ClaseConceptoId] int NULL,
    [TipoCalculo] tinyint NULL,
    [TipoConcepto] tinyint NULL,
    [EsVisible] bit NULL,
    [PorcentajeCalculo] float NULL,
    [CuentaContableId] int NULL,
    [TerceroId] int NULL,
    [BaseLiqSalario] bit NULL,
    [BaseLiqSaludPension] bit NULL,
    [BaseLiqPrimas] bit NULL,
    [BaseLiqCesantias] bit NULL,
    [BaseLiqVacaciones] bit NULL,
    [BaseLiqIndemnizacion] bit NULL,
    [BaseLiqAuxilioTransporte] bit NULL,
    [BaseLiqVacacionesDisfrutadas] bit NULL,
    [BaseLiqVacacionesCompensadas] bit NULL,
    [TipoProcesamientoConcepto] tinyint NULL,
    [Orden] tinyint NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_Nomina_Conceptos PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Nomina_Empleados (
    EmpresaId int NOT NULL CONSTRAINT FK_Nomina_Empleados_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [TerceroId] int NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_Nomina_Empleados PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.Nomina_Contratos (
    EmpresaId int NOT NULL CONSTRAINT FK_Nomina_Contratos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [Codigo] varchar(10) NULL,
    [EmpleadoId] int NULL,
    [TipoContratoId] int NULL,
    [CargoId] int NULL,
    [CentroCostoPpalId] int NULL,
    [FechaInicioContrato] datetime NULL,
    [FechaFinalizacionContrato] datetime NULL,
    [FechaSalida] datetime NULL,
    [EstaActivo] bit NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_Nomina_Contratos PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.dbo_AutorizacionesDIAN (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_AutorizacionesDIAN_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [Modalidad] tinyint NULL,
    [Prefijo] varchar(5) NULL,
    [FechaResolucion] datetime NULL,
    [NumeroResolucion] float NULL,
    [TipoResolucionId] int NULL,
    [FechaFinVigencia] datetime NULL,
    [NumeracionDesde] float NULL,
    [NumeracionHasta] float NULL,
    [NumeracionActual] float NULL,
    [Observacion] varchar(max) NULL,
    [SedeId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_dbo_AutorizacionesDIAN PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.dbo_AutorizacionesDIANSedesComprobantes (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_AutorizacionesDIANSedesComprobantes_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [SedeId] int NULL,
    [TipoComprobanteId] int NULL,
    [AutorizacionDIANId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    [EsAutorizacionDIANAnulacion] bit NULL,
    CONSTRAINT PK_dbo_AutorizacionesDIANSedesComprobantes PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_Terceros (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_Terceros_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [TipoIdentificacionId] int NULL,
    [Identificacion] varchar(30) NULL,
    [DigitoVerificacion] varchar(1) NULL,
    [RazonSocial] varchar(200) NULL,
    [NombreCorto] varchar(100) NULL,
    [EmailFacturaElectronica] varchar(60) NULL,
    [ClaseTercero] smallint NULL,
    [TipoPersona] tinyint NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_SIIFNet_Terceros PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.dbo_pCajas (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pCajas_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [CodCaja] varchar(3) NOT NULL,
    [NomCaja] varchar(30) NULL,
    [VrAcumulado] float NULL,
    [NoRecibo] int NULL,
    [CodTipoCaja] smallint NULL,
    [IdSede] int NULL,
    [IdcUsuario] int NULL,
    [SaldoApertura] float NULL,
    [FormaPagoId] int NULL,
    [FechaDigitacion] smalldatetime NULL,
    [IdCuenta] varchar(50) NULL,
    CONSTRAINT PK_dbo_pCajas PRIMARY KEY (EmpresaId, [CodCaja])
);
GO
CREATE TABLE saludips.SIIFNet_FormasPagos (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_FormasPagos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [Nombre] varchar(max) NULL,
    [ModuloId] int NULL,
    [Clase] tinyint NULL,
    [TipoPago] tinyint NULL,
    [PorDefecto] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_SIIFNet_FormasPagos PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.dbo_dCitasHorarios (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_dCitasHorarios_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [IdMedico] varchar(20) NOT NULL,
    [FechaHorario] smalldatetime NOT NULL,
    [DiaHorario] tinyint NOT NULL,
    [JornadaHorario] varchar(10) NOT NULL,
    [HoraHorario] varchar(5) NOT NULL,
    [CodEspecialidad] smallint NOT NULL,
    [EMConsecutivo] int NULL,
    [MinutosPaciente] tinyint NULL,
    [NoPacientes] tinyint NULL,
    [FechaFinal] smalldatetime NULL,
    [idsede] int NULL,
    [IdHora] int NULL,
    [IdcUsuarioCrea] smallint NULL,
    [FechaCrea] datetime NULL,
    [IdcUsuarioMod] smallint NULL,
    [FechaMod] datetime NULL,
    CONSTRAINT PK_dbo_dCitasHorarios PRIMARY KEY (EmpresaId, [IdMedico], [FechaHorario], [DiaHorario], [JornadaHorario], [HoraHorario], [CodEspecialidad])
);
GO
CREATE TABLE saludips.dbo_mPersonal (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_mPersonal_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [IdPersonal] varchar(20) NOT NULL,
    [NomPersonal] varchar(80) NULL,
    [CodEspecialidad] int NULL,
    [Activo] smallint NULL,
    [Servicios] varchar(255) NULL,
    [CodCargo] int NULL,
    [IdSede] varchar(255) NULL,
    [IdcUsuario] int NULL,
    [FechaDigitacion] smalldatetime NULL,
    [NoRegistro] varchar(20) NULL,
    [PApellido] varchar(60) NULL,
    [SApellido] varchar(60) NULL,
    [PNombre] varchar(60) NULL,
    [SNombre] varchar(60) NULL,
    [Cirujano] smallint NULL,
    [Anestesiologo] smallint NULL,
    [Ayudante] smallint NULL,
    [Instrumentador] smallint NULL,
    [Circulante] smallint NULL,
    [Terapeuta] smallint NULL,
    [Agenda] smallint NULL,
    [Ginecologo] smallint NULL,
    [Pediatra] smallint NULL,
    [TerceroId] int NULL,
    CONSTRAINT PK_dbo_mPersonal PRIMARY KEY (EmpresaId, [IdPersonal])
);
GO
CREATE TABLE saludips.dbo_pConsultorios (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_pConsultorios_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [IdConsultorio] varchar(10) NOT NULL,
    [NomConsultorio] varchar(20) NULL,
    [TipoConsultorio] int NULL,
    [IdSede] int NULL,
    [IdcUsuario] smallint NULL,
    [FechaDigitacion] smalldatetime NULL,
    [Numero] smallint NULL,
    [Parametros] varchar(255) NULL,
    [TipoSala] int NULL,
    CONSTRAINT PK_dbo_pConsultorios PRIMARY KEY (EmpresaId, [IdConsultorio])
);
GO
CREATE TABLE saludips.dbo_mCitasFestivo (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_mCitasFestivo_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [FechaFestivo] smalldatetime NOT NULL,
    CONSTRAINT PK_dbo_mCitasFestivo PRIMARY KEY (EmpresaId, [FechaFestivo])
);
GO
CREATE TABLE saludips.SIIFNet_Parametros (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_Parametros_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [Vigencia] smallint NULL,
    [CodParametro] varchar(100) NULL,
    [Detalle] varchar(max) NULL,
    [Valor] varchar(512) NULL,
    [Protegido] bit NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_SIIFNet_Parametros PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.dbo_cConfiguracionAnual (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_cConfiguracionAnual_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [AnoParametro] varchar(4) NOT NULL,
    [CodParametro] smallint NOT NULL,
    [ValorParametro] varchar(255) NULL,
    [IdcUsuario] int NULL,
    [FechaDigitacion] smalldatetime NULL,
    CONSTRAINT PK_dbo_cConfiguracionAnual PRIMARY KEY (EmpresaId, [AnoParametro], [CodParametro])
);
GO
CREATE TABLE saludips.dbo_cAdministracion (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_cAdministracion_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [IdSede] int NOT NULL,
    [CodInstitucion] varchar(12) NULL,
    [CodDocIdentidadE] varchar(2) NULL,
    [NitInstitucion] varchar(15) NULL,
    [NombreInstitucion] varchar(200) NULL,
    [CodCiudad] varchar(5) NULL,
    [Ciudad] varchar(50) NULL,
    [Direccion] varchar(50) NULL,
    [Telefono] varchar(20) NULL,
    [Fax] varchar(20) NULL,
    [NoFactura] float NULL,
    [NivelAtencion] varchar(1) NULL,
    [Dian] varchar(100) NULL,
    [Version] varchar(10) NULL,
    [PrefijoFactura] varchar(10) NULL,
    [CerosIzquierda] varchar(10) NULL,
    [IdFactura] varchar(10) NULL,
    [ImpresoPor] varchar(40) NULL,
    [IdFacturaDos] varchar(2) NULL,
    [IdReciboWin] varchar(2) NULL,
    [IdReciboDos] varchar(2) NULL,
    [IdReciboConDos] varchar(2) NULL,
    [IdReciboConWin] varchar(2) NULL,
    [IdAdmision] float NULL,
    [IdServicio] float NULL,
    [CCostoSede] varchar(12) NULL,
    [IdEmpresaParticulares] int NULL,
    [Resolucion4505] varchar(2) NULL,
    [FechaVersion] smalldatetime NULL,
    [NoFacturaAl] float NULL,
    [NoResolucionDian] varchar(255) NULL,
    [AutorizacionDIANId] int NULL,
    [NombreCorto] varchar(max) NULL,
    [CodPrestadorSispro] varchar(12) NULL,
    [AutorizacionDIANIdRecibo] int NULL,
    CONSTRAINT PK_dbo_cAdministracion PRIMARY KEY (EmpresaId, [IdSede])
);
GO
CREATE TABLE saludips.dbo_cUsuarios (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_cUsuarios_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [IdcUsuario] smallint NOT NULL,
    [Usuario] varchar(100) NULL,
    [NombresCompletos] varchar(35) NULL,
    [Descripcion] varchar(50) NULL,
    [Activo] smallint NULL,
    [Facturacion] varchar(255) NULL,
    [Cajas] varchar(255) NULL,
    [Citas] varchar(255) NULL,
    [Mantenimiento] varchar(500) NULL,
    [Contratacion] varchar(255) NULL,
    [Historias] varchar(255) NULL,
    [ConfPersonal] varchar(255) NULL,
    [CodCaja] varchar(3) NULL,
    [Kardex] varchar(255) NULL,
    [PyP] varchar(255) NULL,
    [Tarjetero] varchar(255) NULL,
    [Cuentas] varchar(255) NULL,
    [Reportes] varchar(255) NULL,
    [Interfases] varchar(255) NULL,
    [Laboratorio] varchar(255) NULL,
    [Edicion] varchar(255) NULL,
    [IdSede] int NULL,
    [TipoUsu] varchar(2) NULL,
    [CodBodega] varchar(2) NULL,
    [Conectado] int NULL,
    [Quirofanos] varchar(255) NULL,
    [ReportesExtSpool] varchar(350) NULL,
    [Trazabilidad] varchar(255) NULL,
    [AdministracionHC] varchar(255) NULL,
    [FechaVigenciaIngreso] datetime NULL,
    CONSTRAINT PK_dbo_cUsuarios PRIMARY KEY (EmpresaId, [IdcUsuario])
);
GO
CREATE TABLE saludips.dbo_AspNetUsers (
    EmpresaId int NOT NULL CONSTRAINT FK_dbo_AspNetUsers_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] nvarchar(256) NOT NULL,
    [TerceroId] int NULL,
    [BodegaPrincipalId] int NULL,
    [CodCajaPrincipal] nvarchar(max) NULL,
    [Descripcion] nvarchar(max) NULL,
    [Bloqueado] bit NULL,
    [TwoFactorEnabled] bit NULL,
    [LockoutEndDateUtc] datetime NULL,
    [LockoutEnabled] bit NULL,
    [AccessFailedCount] int NULL,
    [UserName] nvarchar(512) NULL,
    CONSTRAINT PK_dbo_AspNetUsers PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_PermisosUsuarios (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_PermisosUsuarios_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [UsuarioSistemaId] varchar(max) NULL,
    [PermisoId] int NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_SIIFNet_PermisosUsuarios PRIMARY KEY (EmpresaId, [Id])
);
GO
CREATE TABLE saludips.SIIFNet_Permisos (
    EmpresaId int NOT NULL CONSTRAINT FK_SIIFNet_Permisos_Empresa REFERENCES seg.Empresa,
    CargaId bigint NOT NULL,
    [Id] int NOT NULL,
    [ModuloId] int NULL,
    [Codigo] varchar(100) NULL,
    [Nombre] varchar(100) NULL,
    [Descripcion] varchar(max) NULL,
    [DisplayOrder] real NULL,
    [PermisoSaludIPS] varchar(10) NULL,
    [IsVisible] bit NULL,
    [FechaDigitacion] datetime NULL,
    [EstadoEntidad] tinyint NULL,
    CONSTRAINT PK_SIIFNet_Permisos PRIMARY KEY (EmpresaId, [Id])
);
GO
-- 52 tablas
