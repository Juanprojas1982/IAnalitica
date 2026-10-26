namespace IAnalitica.Infrastructure.Ingesta;

/// <summary>Columna de un catálogo: nombre en el JSON, columna destino y tipo SQL (se usa en el staging y en la lectura con OPENJSON).</summary>
internal sealed record Col(string Json, string Sql, string Tipo, bool Requerida = false, string? Defecto = null, string[]? Permitidos = null);

/// <summary>Referencia por código a otra dimensión. Requerida: si no existe, la fila se rechaza; opcional: queda en NULL (contrato 05 §4).</summary>
internal sealed record Ref(string Json, string DimTabla, string DimClave, string DimId, string DestinoId, bool Requerida = false, string TipoCodigo = "varchar(30)");

/// <summary>Definición de un catálogo del contrato 05 §5. Las cadenas son constantes del código: nunca vienen de la solicitud.</summary>
internal sealed record DefCatalogo(
    string Entidad, string Tabla, string ClaveSql, string ClaveJson, string ClaveTipo, Col[] Cols, Ref[]? Refs = null,
    string? ColActivo = null, string? DespuesDeLeer = null, string? DespuesDeAplicar = null);

internal static class DefinicionesCatalogo
{
    private static readonly string[] SiNo = { "0", "1" };

    private static Col Txt(string json, string sql, int largo, bool req = false) => new(json, sql, $"nvarchar({largo})", req);
    private static Col Cod(string json, string sql, int largo, bool req = false) => new(json, sql, $"varchar({largo})", req);
    private static Col Bit(string json, string sql, string defecto) => new(json, sql, "bit", false, defecto);

    public static readonly IReadOnlyList<DefCatalogo> Todas = new DefCatalogo[]
    {
        new("sedes", "dim.Sede", "CodigoOrigen", "codigoOrigen", "varchar(30)",
            new[] { Txt("nombre", "Nombre", 150, true), Txt("nombreCorto", "NombreCorto", 60), Bit("activa", "Activa", "1") }, ColActivo: "Activa"),

        new("lineas-servicio", "dim.LineaServicio", "CodigoOrigen", "codigoOrigen", "varchar(30)",
            new[] { Txt("nombre", "Nombre", 120, true) }),

        new("centros-costo", "dim.CentroCosto", "CodigoOrigen", "codigoOrigen", "varchar(30)",
            new[]
            {
                Txt("nombre", "Nombre", 150, true), Cod("codigoPadre", "CodigoPadre", 30),
                new Col("nivel", "Nivel", "tinyint", false, "1"), Cod("tipoOrigen", "TipoOrigen", 20),
                Bit("manejaMovimientos", "ManejaMovimientos", "1"), Txt("unidadNegocio", "UnidadNegocio", 100), Bit("activo", "Activo", "1"),
            }, ColActivo: "Activo"),

        new("cuentas", "dim.CuentaContable", "Codigo", "codigo", "varchar(25)",
            new[]
            {
                Txt("nombre", "Nombre", 200, true), new Col("nivel", "Nivel", "tinyint", true), Cod("codigoPadre", "CodigoPadre", 25),
                new Col("naturaleza", "Naturaleza", "char(1)", true, null, new[] { "D", "C" }),
                Bit("movimientoContrario", "MovimientoContrario", "0"),
                new Col("detalleCartera", "DetalleCartera", "tinyint", false, "0", new[] { "0", "1", "2" }),
                Bit("esBanco", "EsBanco", "0"), Bit("manejaCentroCosto", "ManejaCentroCosto", "0"), Bit("aceptaMovimiento", "AceptaMovimiento", "1"),
            }),

        new("tipos-comprobante", "dim.TipoComprobante", "CodigoOrigen", "codigoOrigen", "varchar(20)",
            new[]
            {
                Cod("prefijo", "Prefijo", 20, true), Txt("nombre", "Nombre", 150, true),
                new Col("claseSugerida", "Clase", "varchar(15)", false, "'NORMAL'",
                    new[] { "NORMAL", "SALDO_INICIAL", "CIERRE", "CIERRE_COSTOS", "TRASLADO", "NOMINA", "FACTURA", "OTRO" }),
            }),

        // El nombre de un paciente nunca se guarda (CK_Tercero_Paciente); la API ya rechaza la carga que lo trae (ReglasPrivacidad).
        new("terceros", "dim.Tercero", "CodigoOrigen", "codigoOrigen", "varchar(30)",
            new[]
            {
                new Col("tipoPersona", "TipoPersona", "char(1)", false, "'J'", new[] { "N", "J" }),
                Cod("identificacion", "Identificacion", 64, true), new Col("digitoVerificacion", "DigitoVerificacion", "char(1)"),
                Txt("nombre", "Nombre", 250), new Col("esPaciente", "EsPaciente", "bit", true), Cod("claseOrigen", "ClaseOrigen", 20),
            }, DespuesDeLeer: "UPDATE #s SET [Nombre] = NULL WHERE [EsPaciente] = 1;"),

        new("pagadores", "dim.Pagador", "CodigoOrigen", "codigoOrigen", "varchar(30)",
            new[] { Txt("nombre", "Nombre", 200, true), Cod("codigoAdministradora", "CodigoAdministradora", 20), Bit("esParticular", "EsParticular", "0"), Bit("activo", "Activo", "1") },
            Refs: new[] { new Ref("terceroCodigo", "dim.Tercero", "CodigoOrigen", "TerceroId", "TerceroId") },
            ColActivo: "Activo",
            DespuesDeAplicar: """
                UPDATE t SET EsPagador = 1 FROM dim.Tercero t
                WHERE t.EmpresaId = @Emp AND t.EsPagador = 0 AND EXISTS (SELECT 1 FROM dim.Pagador p WHERE p.EmpresaId = @Emp AND p.TerceroId = t.TerceroId);
                """),

        new("contratos", "dim.Contrato", "CodigoOrigen", "codigoOrigen", "varchar(30)",
            new[]
            {
                Cod("numeroContrato", "NumeroContrato", 40, true),
                new Col("modalidad", "Modalidad", "varchar(12)", true, null, new[] { "EVENTO", "CAPITACION", "PGP", "PAQUETE", "OTRO" }),
                new Col("fechaInicio", "FechaInicio", "date"), new Col("fechaFin", "FechaFin", "date"), new Col("valorContrato", "ValorContrato", "decimal(19,4)"),
            },
            Refs: new[] { new Ref("pagadorCodigo", "dim.Pagador", "CodigoOrigen", "PagadorId", "PagadorId") }),

        new("servicios", "dim.Servicio", "CodigoOrigen", "codigoOrigen", "varchar(40)",
            new[] { Txt("nombre", "Nombre", 250, true), Cod("codigoEstandar", "CodigoEstandar", 30), Cod("tipo", "Tipo", 20, true) }),

        new("articulos", "dim.Articulo", "CodigoOrigen", "codigoOrigen", "varchar(40)",
            new[]
            {
                Txt("nombre", "Nombre", 250, true), Txt("linea", "Linea", 100), Txt("grupo", "Grupo", 100), Txt("subgrupo", "SubGrupo", 100),
                Txt("generico", "Generico", 150), Txt("laboratorio", "Laboratorio", 150), Txt("unidadMedida", "UnidadMedida", 40), Cod("codigoCum", "CodigoCum", 30),
            }),

        new("lotes", "dim.Lote", "CodigoOrigen", "codigoOrigen", "varchar(30)",
            new[] { Cod("numeroLote", "NumeroLote", 40, true), new Col("fechaVencimiento", "FechaVencimiento", "date"), Cod("registroSanitario", "RegistroSanitario", 60) },
            Refs: new[] { new Ref("articuloCodigo", "dim.Articulo", "CodigoOrigen", "ArticuloId", "ArticuloId", Requerida: true, TipoCodigo: "varchar(40)") }),

        new("almacenes", "dim.Almacen", "CodigoOrigen", "codigoOrigen", "varchar(30)",
            new[] { Txt("nombre", "Nombre", 150, true), Bit("activo", "Activo", "1") },
            Refs: new[] { new Ref("sedeCodigo", "dim.Sede", "CodigoOrigen", "SedeId", "SedeId") }, ColActivo: "Activo"),

        new("especialidades", "dim.Especialidad", "CodigoOrigen", "codigoOrigen", "varchar(20)",
            new[] { Txt("nombre", "Nombre", 120, true) }),

        new("profesionales", "dim.Profesional", "CodigoOrigen", "codigoOrigen", "varchar(30)",
            new[] { Txt("nombre", "Nombre", 150, true), Cod("terceroCodigo", "TerceroCodigo", 30), Bit("activo", "Activo", "1") },
            Refs: new[] { new Ref("especialidadCodigo", "dim.Especialidad", "CodigoOrigen", "EspecialidadId", "EspecialidadId", TipoCodigo: "varchar(20)") },
            ColActivo: "Activo",
            DespuesDeAplicar: """
                UPDATE p SET Especialidad = e.Nombre FROM dim.Profesional p
                JOIN dim.Especialidad e ON e.EspecialidadId = p.EspecialidadId WHERE p.EmpresaId = @Emp;
                """),

        new("usuarios-operativos", "dim.UsuarioOperativo", "CodigoOrigen", "codigoOrigen", "varchar(40)",
            new[] { Txt("nombre", "Nombre", 150, true), Bit("activo", "Activo", "1") }, ColActivo: "Activo"),

        new("cuentas-bancarias", "dim.CuentaBancaria", "CodigoOrigen", "codigoOrigen", "varchar(30)",
            new[]
            {
                new Col("tipo", "Tipo", "varchar(10)", true, null, new[] { "BANCO", "CAJA" }), Txt("entidad", "Entidad", 100, true), Cod("numeroEnmascarado", "NumeroEnmascarado", 20),
            },
            Refs: new[] { new Ref("cuentaCodigo", "dim.CuentaContable", "Codigo", "CuentaId", "CuentaId", TipoCodigo: "varchar(25)") }),
    };

    public static DefCatalogo? Buscar(string entidad) => Todas.FirstOrDefault(d => d.Entidad == entidad);
}
