namespace IAnalitica.Ingesta.Contracts;

/// <summary>Valores de «modo» del contrato 05 §3.</summary>
public static class Modos
{
    public const string Completa = "COMPLETA";
    public const string Ventana = "VENTANA";
    public const string Incremental = "INCREMENTAL";
    public const string Foto = "FOTO";
    public const string Inicial = "INICIAL";
}

/// <summary>Estados de una carga (24 §6). La carga es atómica: no existe un estado parcial.</summary>
public static class EstadosCarga
{
    public const string Abierta = "ABIERTA";
    public const string Cerrada = "CERRADA";
    public const string Procesando = "PROCESANDO";
    public const string Ok = "OK";
    public const string Error = "ERROR";
    public const string Rechazada = "RECHAZADA";
}

/// <summary>Códigos estables de error (extensión «codigo» del Problem Details, spec 05 §7).</summary>
public static class CodigosError
{
    public const string ContratoInvalido = "CONTRATO_INVALIDO";
    public const string EntidadNoSoportada = "ENTIDAD_NO_SOPORTADA";
    public const string CargaDuplicada = "CARGA_DUPLICADA";
    public const string PeriodoCerrado = "PERIODO_CERRADO";
    public const string LoteGrande = "LOTE_GRANDE";
    public const string ValidacionNegocio = "VALIDACION_NEGOCIO";
    public const string ControlNoCuadra = "CONTROL_NO_CUADRA";
    public const string CargaNoAbierta = "CARGA_NO_ABIERTA";
    public const string LimiteExcedido = "LIMITE_EXCEDIDO";
}

/// <summary>POST /v1/cargas. El EmpresaId NO viaja aquí: sale del token.</summary>
public sealed class SolicitudCarga
{
    public string Entidad { get; set; } = "";
    public string Modo { get; set; } = "";
    public DateTime? VentanaDesde { get; set; }
    public DateTime? VentanaHasta { get; set; }
    public DateTime? Corte { get; set; }
    public string? VersionAgente { get; set; }
    public string? VersionFuente { get; set; }
}

public sealed class CargaCreada
{
    public long CargaId { get; set; }
}

public sealed class RechazoFila
{
    public string? SourceKey { get; set; }
    public string Motivo { get; set; } = "";
}

/// <summary>202 de POST /v1/cargas/{id}/lotes/{n}.</summary>
public sealed class RespuestaLote
{
    public int FilasRecibidas { get; set; }
    public List<RechazoFila> RechazadasValidacion { get; set; } = new List<RechazoFila>();
}

/// <summary>POST /v1/cargas/{id}/cerrar.</summary>
public sealed class CierreCarga
{
    public int TotalLotes { get; set; }
    public int TotalFilas { get; set; }
    public decimal? SumaControl { get; set; }
}

/// <summary>GET /v1/cargas/{id}.</summary>
public sealed class EstadoCarga
{
    public long CargaId { get; set; }
    public string Entidad { get; set; } = "";
    public string Estado { get; set; } = "";
    public int FilasInsertadas { get; set; }
    public int FilasActualizadas { get; set; }
    public int FilasEliminadas { get; set; }
    public int FilasRechazadas { get; set; }
    public string? Error { get; set; }
}

/// <summary>GET /v1/estado: última carga correcta por entidad (el conector decide su ventana).</summary>
public sealed class MarcaAgua
{
    public string Entidad { get; set; } = "";
    public DateTime? UltimaCargaOkEn { get; set; }
    public long? CargaId { get; set; }
}

public sealed class EstadoPlataforma
{
    public string? Empresa { get; set; }
    public List<MarcaAgua> Entidades { get; set; } = new List<MarcaAgua>();
}
