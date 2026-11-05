using Microsoft.Extensions.AI;

namespace IAnalitica.Application.Asistente;

public sealed record ConfigAsistente(
    bool Habilitado, short ModeloPrincipalId, short? ModeloRespaldoId, short? ModeloAhorroId, byte? AhorroDesdePct,
    int MaxTokensSalidaPorLlamada, byte MaxIteracionesHerramientas, int MaxTokensHistorial, bool GuardarTextoConversacion, string VisibilidadCosto);

public sealed record ModeloIa(
    short ModeloId, string Codigo, string ProveedorCodigo, decimal PrecioEntradaMTok, decimal PrecioSalidaMTok, decimal? PrecioCacheLecturaMTok, bool SoportaHerramientas, bool Activo);

/// <summary>Tarifa de venta vigente en COP por 1 millón de tokens (<c>ia.Tarifa</c>).</summary>
public sealed record TarifaVenta(int TarifaId, decimal EntradaCop, decimal SalidaCop, decimal CacheLecturaCop);

public sealed record ConsumoMes(decimal CupoCop, decimal ValorConsumoCop, decimal ReservadoCop, bool EnExcedente);

public sealed record ReservaCuota(Guid? ReservaId, string Resultado)
{
    public bool Concedida => Resultado is "OK" or "OK_EXCEDENTE";
    public bool EnExcedente => Resultado == "OK_EXCEDENTE";
}

public sealed record MensajeGuardado(string Rol, string? Texto);

public sealed record UsoLlamada(
    Guid? ConversacionId, long? MensajeId, short ModeloId, int TarifaId, byte Iteracion, int TokensEntrada, int TokensSalida, int TokensCacheLectura,
    decimal CostoProveedorUsd, decimal ValorVentaCop, bool EnExcedente, int LatenciaMs, string? Herramientas, string Resultado);

/// <summary>Datos del asistente con el contexto del usuario (RLS por empresa).</summary>
public interface IRepositorioAsistente
{
    Task<ConfigAsistente?> ObtenerConfigAsync(CancellationToken ct);
    Task<ModeloIa?> ObtenerModeloAsync(short modeloId, CancellationToken ct);
    Task<string?> ObtenerPromptActivoAsync(CancellationToken ct);
    Task<TarifaVenta?> ObtenerTarifaVigenteAsync(DateOnly fecha, CancellationToken ct);
    Task<ConsumoMes?> ObtenerConsumoAsync(CancellationToken ct);
    Task<ReservaCuota> ReservarAsync(int usuarioId, short modeloId, decimal montoCop, decimal montoUsd, CancellationToken ct);
    Task LiquidarAsync(Guid reservaId, long tokensEntrada, long tokensSalida, long tokensCache, decimal costoUsd, decimal valorCop, CancellationToken ct);
    Task<Guid> CrearConversacionAsync(int usuarioId, string titulo, string? dashboard, CancellationToken ct);
    /// <summary>false si la conversación no existe o no es del usuario.</summary>
    Task<bool> ConversacionEsDelUsuarioAsync(Guid conversacionId, int usuarioId, CancellationToken ct);
    Task<IReadOnlyList<MensajeGuardado>> HistorialAsync(Guid conversacionId, int maximo, CancellationToken ct);
    Task<long> GuardarMensajeAsync(Guid conversacionId, string rol, string? texto, string? contextoJson, string? fuentesJson, decimal? valorCop, CancellationToken ct);
    Task RegistrarUsoAsync(UsoLlamada uso, int usuarioId, CancellationToken ct);
}

/// <summary>Resuelve el cliente de chat de un modelo. No hay modelos fijos en el código (spec 07 §3). null = proveedor sin configurar en este entorno.</summary>
public interface IAsistenteChatClientFactory
{
    IChatClient? Crear(string proveedorCodigo, string modeloCodigo);
}

public sealed record FuenteRespuesta(string Metrica, string Nombre, string Parametros);

public sealed record RespuestaAsistente(
    Guid ConversacionId, string Texto, IReadOnlyList<FuenteRespuesta> Fuentes, decimal ValorCop, bool EnExcedente, bool Parcial, bool MostrarValor = false);
