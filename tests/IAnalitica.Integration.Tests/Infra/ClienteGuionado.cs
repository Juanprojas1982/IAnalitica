using Microsoft.Extensions.AI;

namespace IAnalitica.Integration.Tests.Infra;

/// <summary>Cliente de chat guionado: cada llamada devuelve la siguiente respuesta de la cola y guarda lo que recibió.</summary>
public sealed class ClienteGuionado(params Func<IList<ChatMessage>, ChatResponse>[] pasos) : IChatClient
{
    private readonly Queue<Func<IList<ChatMessage>, ChatResponse>> _pasos = new(pasos);
    public List<List<ChatMessage>> Recibidos { get; } = [];
    public int Llamadas { get; private set; }
    public ChatOptions? UltimasOpciones { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Llamadas++;
        UltimasOpciones = options;
        var lista = messages.ToList();
        Recibidos.Add(lista);
        return Task.FromResult(_pasos.Dequeue()(lista));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

