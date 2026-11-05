using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Semantica;
using IAnalitica.Domain.Modulos;
using Microsoft.Extensions.AI;

namespace IAnalitica.Application.Asistente;

public static class CodigosAsistente
{
    public const string ModuloInactivo = "MODULO_INACTIVO";
    public const string ProveedorNoConfigurado = "PROVEEDOR_NO_CONFIGURADO";
    public const string TarifaNoConfigurada = "TARIFA_NO_CONFIGURADA";
    public const string ConversacionNoExiste = "CONVERSACION_NO_EXISTE";
    public const string ErrorAsistente = "ASISTENTE_ERROR";
    public const string Modulo = "IA";
}

[RequierePermiso("ia.usar")]
public sealed record EnviarMensajeAsistenteCommand(Guid? ConversacionId, string Texto, string? Dashboard = null, FiltrosDashboard? Filtros = null) : IRequest<Result<RespuestaAsistente>>;

public sealed class EnviarMensajeAsistenteValidator : AbstractValidator<EnviarMensajeAsistenteCommand>
{
    public const int MaxCaracteres = 2000;

    public EnviarMensajeAsistenteValidator()
    {
        RuleFor(x => x.Texto).NotEmpty().WithErrorCode("PREGUNTA_REQUERIDA").WithMessage("Escriba su pregunta.")
            .MaximumLength(MaxCaracteres).WithErrorCode("PREGUNTA_LARGA").WithMessage($"La pregunta admite hasta {MaxCaracteres} caracteres.");
    }
}

/// <summary>
/// Orquestador del asistente (spec 07 §3 y §6.6; 24 §5.7): autoriza, elige modelo, estima y reserva cuota, arma el prompt, corre el bucle de herramientas
/// con tope de iteraciones, valora con la tarifa vigente, liquida y entrega la respuesta sanitizada y con los nombres re-hidratados.
/// Si el proveedor falla, la reserva se liquida con lo consumido hasta ese punto. Nunca se llama al proveedor sin una reserva concedida.
/// </summary>
public sealed class EnviarMensajeAsistenteHandler(
    ISesionUsuario sesion, IModulosEmpresa modulos, IRepositorioAsistente repo, IAsistenteChatClientFactory fabrica,
    CatalogoAnalitico catalogo, IMotorMetricas motor, TimeProvider reloj) : IRequestHandler<EnviarMensajeAsistenteCommand, Result<RespuestaAsistente>>
{
    private const int CaracteresPorToken = 3;            // estimación conservadora para español
    private const int TokensDeHerramientas = 1500;       // definiciones de las herramientas + sobrecarga del prompt
    private const decimal Millon = 1_000_000m;

    private sealed record Acumulado(long Entrada, long Salida, long Cache, decimal CostoUsd, decimal ValorCop);

    public async Task<Result<RespuestaAsistente>> Handle(EnviarMensajeAsistenteCommand request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is not { } usuarioId) return Result<RespuestaAsistente>.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        if (!await modulos.EstaActivoAsync(empresaId, CodigosAsistente.Modulo, ct).ConfigureAwait(false))
            return Falla(CodigosAsistente.ModuloInactivo, "El asistente no está activo para su empresa.");
        var config = await repo.ObtenerConfigAsync(ct).ConfigureAwait(false);
        if (config is not { Habilitado: true }) return Falla("DESHABILITADO", MensajeReserva("DESHABILITADO"));

        // Modelo: el de ahorro cuando el consumo del mes alcanzó el porcentaje configurado; si no, el principal
        var consumo = await repo.ObtenerConsumoAsync(ct).ConfigureAwait(false);
        var usarAhorro = config.ModeloAhorroId is not null && config.AhorroDesdePct is { } pct && consumo is { CupoCop: > 0 } c && 100m * c.ValorConsumoCop / c.CupoCop >= pct;
        var modelo = await repo.ObtenerModeloAsync(usarAhorro ? config.ModeloAhorroId!.Value : config.ModeloPrincipalId, ct).ConfigureAwait(false);
        if (modelo is not { Activo: true, SoportaHerramientas: true }) return Falla(CodigosAsistente.ProveedorNoConfigurado, "El modelo del asistente no está disponible.");
        var cliente = fabrica.Crear(modelo.ProveedorCodigo, modelo.Codigo);
        if (cliente is null) return Falla(CodigosAsistente.ProveedorNoConfigurado, "No hay un proveedor de IA configurado en este entorno.");
        var hoy = DateOnly.FromDateTime(reloj.GetUtcNow().UtcDateTime.AddHours(-5));   // Colombia: UTC-5 todo el año
        var tarifa = await repo.ObtenerTarifaVigenteAsync(hoy, ct).ConfigureAwait(false);
        if (tarifa is null) return Falla(CodigosAsistente.TarifaNoConfigurada, "Falta configurar la tarifa de venta del asistente.");

        var conversacionId = request.ConversacionId;
        if (conversacionId is { } existente)
        {
            if (!await repo.ConversacionEsDelUsuarioAsync(existente, usuarioId, ct).ConfigureAwait(false)) return Result<RespuestaAsistente>.Falla(Error.NoEncontrado("La conversación no existe."));
        }
        else conversacionId = await repo.CrearConversacionAsync(usuarioId, Titulo(request.Texto), request.Dashboard, ct).ConfigureAwait(false);

        var prompt = await repo.ObtenerPromptActivoAsync(ct).ConfigureAwait(false) ?? PromptPorDefecto;
        var historial = await repo.HistorialAsync(conversacionId.Value, 40, ct).ConfigureAwait(false);

        // Estimar el máximo de la pregunta y reservarlo (spec 07 §6.6): sin reserva no se llama al proveedor
        var iteraciones = Math.Max((int)config.MaxIteracionesHerramientas, 1);
        var entradaEstimada = TokensDeHerramientas + (prompt.Length + request.Texto.Length + historial.Sum(m => m.Texto?.Length ?? 0)) / CaracteresPorToken;
        entradaEstimada = Math.Min(entradaEstimada, config.MaxTokensHistorial + TokensDeHerramientas + (prompt.Length + request.Texto.Length) / CaracteresPorToken);
        var salidaMaxima = config.MaxTokensSalidaPorLlamada * iteraciones;
        var reservaCop = Valorar(tarifa.EntradaCop, entradaEstimada * (long)iteraciones) + Valorar(tarifa.SalidaCop, salidaMaxima);
        var reservaUsd = Valorar(modelo.PrecioEntradaMTok, entradaEstimada * (long)iteraciones) + Valorar(modelo.PrecioSalidaMTok, salidaMaxima);
        var reserva = await repo.ReservarAsync(usuarioId, modelo.ModeloId, Math.Round(reservaCop, 2), Math.Round(reservaUsd, 6), ct).ConfigureAwait(false);
        if (!reserva.Concedida) return Falla(reserva.Resultado, MensajeReserva(reserva.Resultado));

        var seudonimos = new Seudonimizador();
        var herramientas = new HerramientasAsistente(catalogo, motor, sesion, modulos, seudonimos);
        var acumulado = new Acumulado(0, 0, 0, 0, 0);
        var texto = "";
        var parcial = false;
        string? falloDelProveedor = null;
        try
        {
            var mensajes = ArmarMensajes(prompt, historial, request);
            var opciones = new ChatOptions { Tools = herramientas.Declarar(), MaxOutputTokens = config.MaxTokensSalidaPorLlamada, ToolMode = ChatToolMode.Auto };
            for (byte iteracion = 1; ; iteracion++)
            {
                if (iteracion > iteraciones) { parcial = true; texto = "No alcancé a completar el análisis con el máximo de consultas permitido. Intente con una pregunta más acotada."; break; }
                if (acumulado.ValorCop + Valorar(tarifa.SalidaCop, config.MaxTokensSalidaPorLlamada) > reservaCop)
                {
                    parcial = true; texto = "Detuve el análisis para no superar el consumo reservado para esta pregunta. Intente con una pregunta más acotada.";
                    await repo.RegistrarUsoAsync(new UsoLlamada(conversacionId, null, modelo.ModeloId, tarifa.TarifaId, iteracion, 0, 0, 0, 0, 0, reserva.EnExcedente, 0, null, "CORTADO_TOPE"), usuarioId, ct).ConfigureAwait(false);
                    break;
                }

                var reloj1 = Stopwatch.StartNew();
                ChatResponse respuesta;
                try { respuesta = await cliente.GetResponseAsync(mensajes, opciones, ct).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    falloDelProveedor = e.GetType().Name;
                    await repo.RegistrarUsoAsync(new UsoLlamada(conversacionId, null, modelo.ModeloId, tarifa.TarifaId, iteracion, 0, 0, 0, 0, 0, reserva.EnExcedente, (int)reloj1.ElapsedMilliseconds, null, "ERROR"), usuarioId, ct).ConfigureAwait(false);
                    break;
                }

                var uso = Medir(respuesta.Usage, modelo, tarifa);
                acumulado = new Acumulado(acumulado.Entrada + uso.Entrada, acumulado.Salida + uso.Salida, acumulado.Cache + uso.Cache, acumulado.CostoUsd + uso.CostoUsd, acumulado.ValorCop + uso.ValorCop);
                var llamadas = respuesta.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
                var resultado = respuesta.FinishReason == ChatFinishReason.ContentFilter ? "RECHAZO" : "OK";
                await repo.RegistrarUsoAsync(new UsoLlamada(conversacionId, null, modelo.ModeloId, tarifa.TarifaId, iteracion, (int)uso.Entrada, (int)uso.Salida, (int)uso.Cache, uso.CostoUsd, uso.ValorCop,
                    reserva.EnExcedente, (int)reloj1.ElapsedMilliseconds, string.Join(",", llamadas.Select(l => l.Name).Distinct()).Truncar(400), resultado), usuarioId, ct).ConfigureAwait(false);

                if (resultado == "RECHAZO") { parcial = true; texto = "El modelo no pudo responder a esta consulta. Reformule la pregunta."; break; }
                if (llamadas.Count == 0) { texto = respuesta.Text; break; }

                mensajes.AddRange(respuesta.Messages);
                var resultados = new List<AIContent>();
                foreach (var llamada in llamadas)
                    resultados.Add(new FunctionResultContent(llamada.CallId, await EjecutarAsync(opciones.Tools!, llamada, ct).ConfigureAwait(false)));
                mensajes.Add(new ChatMessage(ChatRole.Tool, resultados));
            }
        }
        finally
        {
            await repo.LiquidarAsync(reserva.ReservaId!.Value, acumulado.Entrada, acumulado.Salida, acumulado.Cache, acumulado.CostoUsd, acumulado.ValorCop, CancellationToken.None).ConfigureAwait(false);
        }

        if (falloDelProveedor is not null) return Falla(CodigosAsistente.ErrorAsistente, "El asistente no pudo responder en este momento. Intente de nuevo en unos minutos.");

        var seudonimizado = SanitizadorSalida.Limpiar(texto);
        if (seudonimizado.Length == 0) seudonimizado = "No obtuve una respuesta del modelo. Intente de nuevo.";
        var limpio = seudonimos.Rehidratar(seudonimizado);
        var guardar = config.GuardarTextoConversacion;
        await repo.GuardarMensajeAsync(conversacionId.Value, "USUARIO", guardar ? request.Texto : null, ContextoJson(request), null, null, ct).ConfigureAwait(false);
        // Lo que se guarda es el texto con alias: el historial vuelve al proveedor en el siguiente turno y no debe llevar nombres reales
        await repo.GuardarMensajeAsync(conversacionId.Value, "ASISTENTE", guardar ? seudonimizado : null, null, JsonSerializer.Serialize(herramientas.Fuentes), acumulado.ValorCop, ct).ConfigureAwait(false);
        // Visibilidad del valor (spec 07 §6.4): el administrador siempre; los demás si la empresa lo configuró para todos
        var mostrarValor = config.VisibilidadCosto == "TODOS" || sesion.TienePermiso("admin.usuarios");
        return new RespuestaAsistente(conversacionId.Value, limpio, herramientas.Fuentes, acumulado.ValorCop, reserva.EnExcedente, parcial, mostrarValor);
    }

    private static List<ChatMessage> ArmarMensajes(string prompt, IReadOnlyList<MensajeGuardado> historial, EnviarMensajeAsistenteCommand request)
    {
        var mensajes = new List<ChatMessage> { new(ChatRole.System, prompt) };
        foreach (var h in historial.Where(h => !string.IsNullOrEmpty(h.Texto)))
            mensajes.Add(new ChatMessage(h.Rol == "USUARIO" ? ChatRole.User : ChatRole.Assistant, h.Texto));
        var sb = new StringBuilder();
        if (request.Dashboard is not null || request.Filtros is not null)
        {
            // El contexto va en el mensaje del usuario, delimitado, y es información, no instrucciones
            sb.Append("<contexto_dashboard>").Append(request.Dashboard ?? "").Append(' ').Append(request.Filtros?.Clave() ?? "").AppendLine("</contexto_dashboard>");
        }
        sb.Append(request.Texto);
        mensajes.Add(new ChatMessage(ChatRole.User, sb.ToString()));
        return mensajes;
    }

    private static async Task<object?> EjecutarAsync(IList<AITool> herramientas, FunctionCallContent llamada, CancellationToken ct)
    {
        if (herramientas.OfType<AIFunction>().FirstOrDefault(f => f.Name == llamada.Name) is not { } funcion)
            return JsonSerializer.Serialize(new { error = $"La herramienta «{llamada.Name}» no existe." });
        try { return await funcion.InvokeAsync(llamada.Arguments is null ? null : new AIFunctionArguments(llamada.Arguments), ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { return JsonSerializer.Serialize(new { error = "Los parámetros de la herramienta no son válidos: " + e.Message }); }
    }

    private static (long Entrada, long Salida, long Cache, decimal CostoUsd, decimal ValorCop) Medir(UsageDetails? u, ModeloIa m, TarifaVenta t)
    {
        long entrada = u?.InputTokenCount ?? 0, salida = u?.OutputTokenCount ?? 0, cache = u?.CachedInputTokenCount ?? 0;
        var normales = Math.Max(entrada - cache, 0);
        var usd = Valorar(m.PrecioEntradaMTok, normales) + Valorar(m.PrecioCacheLecturaMTok ?? m.PrecioEntradaMTok, cache) + Valorar(m.PrecioSalidaMTok, salida);
        var cop = Valorar(t.EntradaCop, normales) + Valorar(t.CacheLecturaCop, cache) + Valorar(t.SalidaCop, salida);
        return (entrada, salida, cache, Math.Round(usd, 6), Math.Round(cop, 2));
    }

    private static decimal Valorar(decimal precioPorMillon, long tokens) => precioPorMillon * tokens / Millon;

    private static string Titulo(string texto) => texto.Length <= 80 ? texto.Trim() : texto[..80].Trim() + "…";

    private static string ContextoJson(EnviarMensajeAsistenteCommand r)
        => JsonSerializer.Serialize(new { dashboard = r.Dashboard, filtros = r.Filtros?.Clave() });

    private static Result<RespuestaAsistente> Falla(string codigo, string mensaje) => Result<RespuestaAsistente>.Falla(Error.Conflicto(codigo, mensaje));

    public static string MensajeReserva(string resultado) => resultado switch
    {
        "DESHABILITADO" => "El asistente no está habilitado para su empresa.",
        "CUPO_AGOTADO" => "El cupo incluido de este mes se agotó. El asistente vuelve a estar disponible el mes siguiente.",
        "TOPE_EXCEDENTE" => "Se alcanzó el tope de consumo adicional autorizado de este mes.",
        "TOPE_USUARIO" => "Alcanzó su tope diario de consumo del asistente.",
        "TOPE_PLATAFORMA" => "El asistente está temporalmente limitado. Se avisó al administrador de la plataforma.",
        "SIN_CONSENTIMIENTO" => "El proveedor del asistente requiere un consentimiento firmado que aún no está registrado.",
        _ => "No se pudo reservar el consumo del asistente.",
    };

    /// <summary>Respaldo si <c>ia.PromptVersion</c> no tiene una versión activa. La versión de fábrica vive en el script 1030.</summary>
    public const string PromptPorDefecto = "Eres un analista financiero de una IPS colombiana. Toda cifra debe salir de una herramienta; si el dato falta, dilo. Responde en español, conciso.";
}

internal static class TextoExtensions
{
    public static string? Truncar(this string s, int max) => s.Length <= max ? s : s[..max];
}
