namespace IAnalitica.Application.Regulacion;

/// <summary>La circular a la que pertenece una lista de precios máximos (reg.Circular). Los datos los escribe quien importa; nada se infiere del archivo.</summary>
public sealed record DatosCircular(string Entidad, string Numero, short Anio, string Tema, DateOnly FechaExpedicion, DateOnly VigenteDesde, DateOnly? VigenteHasta, string UrlFuente, string? Notas);

/// <summary>
/// Nombres de los campos del archivo de la fuente. NO hay valores por defecto: los campos del dataset «nauz-qkjw» de datos.gov.co no se han podido verificar
/// (spec 15 §1, «por validar») y se configuran en <c>Regulacion:PreciosMaximos:Mapeo</c>. El CUM viene en un solo campo o en expediente + consecutivo.
/// </summary>
public sealed class MapeoPreciosMaximos
{
    public string? CampoCum { get; set; }
    public string? CampoExpediente { get; set; }
    public string? CampoConsecutivo { get; set; }
    public string? CampoPrincipio { get; set; }
    public string? CampoPresentacion { get; set; }
    public string? CampoPrecioPresentacion { get; set; }
    public string? CampoPrecioUnidad { get; set; }
    public string? CampoUnidadesPorPresentacion { get; set; }

    public bool EstaCompleto => !string.IsNullOrWhiteSpace(CampoPrecioPresentacion)
        && (!string.IsNullOrWhiteSpace(CampoCum) || (!string.IsNullOrWhiteSpace(CampoExpediente) && !string.IsNullOrWhiteSpace(CampoConsecutivo)));
}

public sealed record ResultadoImportacion(int CircularId, int Importados, int Rechazados, IReadOnlyList<string> Muestras);

public interface IImportadorPreciosMaximos
{
    /// <param name="json">Arreglo JSON de objetos planos (la exportación JSON de datos.gov.co).</param>
    Task<ResultadoImportacion> ImportarAsync(DatosCircular circular, MapeoPreciosMaximos mapeo, Stream json, CancellationToken ct);
    Task RegistrarFactorAsync(string cum, decimal unidadesPorPresentacion, string validadoPor, CancellationToken ct);
}
