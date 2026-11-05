using System.Data;
using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Asistente;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.Extensions.AI;

namespace IAnalitica.Infrastructure.Asistente;

/// <summary>Datos del asistente con el contexto del usuario (RLS por empresa). La reserva y la liquidación de cuota son los procedimientos <c>ia.usp_*</c>.</summary>
internal sealed class RepositorioAsistente(IFabricaConexiones conexiones, ISesionUsuario sesion) : IRepositorioAsistente
{
    private int EmpresaId => sesion.EmpresaId ?? throw new InvalidOperationException("El asistente exige una empresa en la sesión.");

    public async Task<ConfigAsistente?> ObtenerConfigAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.QueryFirstOrDefaultAsync<ConfigAsistente>(new CommandDefinition("""
            SELECT Habilitado, ModeloPrincipalId, ModeloRespaldoId, ModeloAhorroId, AhorroDesdePct, MaxTokensSalidaPorLlamada, MaxIteracionesHerramientas,
                   MaxTokensHistorial, GuardarTextoConversacion, VisibilidadCosto
            FROM ia.EmpresaConfig WHERE EmpresaId = @empresaId
            """, new { empresaId = EmpresaId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<ModeloIa?> ObtenerModeloAsync(short modeloId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.QueryFirstOrDefaultAsync<ModeloIa>(new CommandDefinition("""
            SELECT m.ModeloId, m.Codigo, p.Codigo AS ProveedorCodigo, m.PrecioEntradaMTok, m.PrecioSalidaMTok, m.PrecioCacheLecturaMTok, m.SoportaHerramientas,
                   CAST(CASE WHEN m.Activo = 1 AND p.Activo = 1 THEN 1 ELSE 0 END AS bit) AS Activo
            FROM ia.Modelo m JOIN ia.Proveedor p ON p.ProveedorId = m.ProveedorId WHERE m.ModeloId = @modeloId
            """, new { modeloId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<string?> ObtenerPromptActivoAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.QueryFirstOrDefaultAsync<string>(new CommandDefinition("SELECT TextoSistema FROM ia.PromptVersion WHERE Activa = 1", cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<TarifaVenta?> ObtenerTarifaVigenteAsync(DateOnly fecha, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.QueryFirstOrDefaultAsync<TarifaVenta>(new CommandDefinition(
            "SELECT TarifaId, ValorMTokEntradaCop AS EntradaCop, ValorMTokSalidaCop AS SalidaCop, ValorMTokCacheLecturaCop AS CacheLecturaCop FROM ia.fn_TarifaVigente(@empresaId, @fecha)",
            new { empresaId = EmpresaId, fecha = fecha.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<ConsumoMes?> ObtenerConsumoAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.QueryFirstOrDefaultAsync<ConsumoMes>(new CommandDefinition("""
            SELECT COALESCE(ec.CupoIncluidoCop, p.CupoIncluidoCop) AS CupoCop, ISNULL(cp.ValorConsumoCop, 0) AS ValorConsumoCop, ISNULL(cp.ReservadoCop, 0) AS ReservadoCop,
                   CAST(CASE WHEN cp.EnExcedenteDesde IS NULL THEN 0 ELSE 1 END AS bit) AS EnExcedente
            FROM ia.EmpresaConfig ec JOIN ia.PlanComercial p ON p.PlanId = ec.PlanId
            LEFT JOIN ia.ConsumoPeriodo cp ON cp.EmpresaId = ec.EmpresaId AND cp.PeriodoKey = YEAR(SYSUTCDATETIME()) * 100 + MONTH(SYSUTCDATETIME())
            WHERE ec.EmpresaId = @empresaId
            """, new { empresaId = EmpresaId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<ReservaCuota> ReservarAsync(int usuarioId, short modeloId, decimal montoCop, decimal montoUsd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        var p = new DynamicParameters();
        p.Add("EmpresaId", EmpresaId); p.Add("UsuarioId", usuarioId); p.Add("ModeloId", modeloId);
        p.Add("MontoCop", montoCop, DbType.Decimal, precision: 16, scale: 2); p.Add("MontoUsd", montoUsd, DbType.Decimal, precision: 14, scale: 6);
        p.Add("ReservaId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        p.Add("Resultado", dbType: DbType.String, size: 20, direction: ParameterDirection.Output);
        await cn.ExecuteAsync(new CommandDefinition("ia.usp_ReservarCuota", p, commandType: CommandType.StoredProcedure, cancellationToken: ct)).ConfigureAwait(false);
        return new ReservaCuota(p.Get<Guid?>("ReservaId"), p.Get<string>("Resultado"));
    }

    public async Task LiquidarAsync(Guid reservaId, long tokensEntrada, long tokensSalida, long tokensCache, decimal costoUsd, decimal valorCop, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition("ia.usp_LiquidarReserva",
            new { EmpresaId, ReservaId = reservaId, TokensEntrada = tokensEntrada, TokensSalida = tokensSalida, TokensCache = tokensCache, CostoUsd = costoUsd, ValorCop = valorCop },
            commandType: CommandType.StoredProcedure, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<Guid> CrearConversacionAsync(int usuarioId, string titulo, string? dashboard, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "INSERT ia.Conversacion (EmpresaId, UsuarioId, Titulo, Dashboard) OUTPUT inserted.ConversacionId VALUES (@empresaId, @usuarioId, @titulo, @dashboard)",
            new { empresaId = EmpresaId, usuarioId, titulo = titulo.Length > 150 ? titulo[..150] : titulo, dashboard = dashboard is { Length: > 40 } ? dashboard[..40] : dashboard }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> ConversacionEsDelUsuarioAsync(Guid conversacionId, int usuarioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM ia.Conversacion WHERE ConversacionId = @conversacionId AND UsuarioId = @usuarioId) THEN 1 ELSE 0 END",
            new { conversacionId, usuarioId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MensajeGuardado>> HistorialAsync(Guid conversacionId, int maximo, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<MensajeGuardado>(new CommandDefinition("""
            SELECT Rol, Texto FROM (SELECT TOP (@maximo) MensajeId, Rol, Texto FROM ia.Mensaje WHERE ConversacionId = @conversacionId ORDER BY MensajeId DESC) x ORDER BY MensajeId
            """, new { conversacionId, maximo }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<long> GuardarMensajeAsync(Guid conversacionId, string rol, string? texto, string? contextoJson, string? fuentesJson, decimal? valorCop, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT ia.Mensaje (ConversacionId, EmpresaId, Rol, Texto, ContextoJson, FuentesJson, ValorCop) OUTPUT inserted.MensajeId
            VALUES (@conversacionId, @empresaId, @rol, @texto, @contextoJson, @fuentesJson, @valorCop);
            """, new { conversacionId, empresaId = EmpresaId, rol, texto, contextoJson, fuentesJson, valorCop }, cancellationToken: ct)).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition("UPDATE ia.Conversacion SET UltimoMensajeEn = SYSUTCDATETIME() WHERE ConversacionId = @conversacionId", new { conversacionId }, cancellationToken: ct)).ConfigureAwait(false);
        return id;
    }

    public async Task RegistrarUsoAsync(UsoLlamada u, int usuarioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT ia.UsoLlamada (EmpresaId, UsuarioId, ConversacionId, MensajeId, ModeloId, TarifaId, Iteracion, TokensEntrada, TokensSalida, TokensCacheLectura, CostoProveedorUsd,
                                  ValorVentaCop, EnExcedente, LatenciaMs, Herramientas, Resultado)
            VALUES (@empresaId, @usuarioId, @ConversacionId, @MensajeId, @ModeloId, @TarifaId, @Iteracion, @TokensEntrada, @TokensSalida, @TokensCacheLectura, @CostoProveedorUsd,
                    @ValorVentaCop, @EnExcedente, @LatenciaMs, @Herramientas, @Resultado)
            """, new
        {
            empresaId = EmpresaId, usuarioId, u.ConversacionId, u.MensajeId, u.ModeloId, u.TarifaId, u.Iteracion, u.TokensEntrada, u.TokensSalida, u.TokensCacheLectura,
            u.CostoProveedorUsd, u.ValorVentaCop, u.EnExcedente, u.LatenciaMs, u.Herramientas, u.Resultado,
        }, cancellationToken: ct)).ConfigureAwait(false);
    }
}

/// <summary>Sin proveedor configurado: en las pruebas internas de la fase 1 no se llama a ningún proveedor real (D32). Un adaptador de Anthropic se registra aquí cuando haya clave y aprobación.</summary>
internal sealed class SinProveedorChatClientFactory : IAsistenteChatClientFactory
{
    public IChatClient? Crear(string proveedorCodigo, string modeloCodigo) => null;
}
