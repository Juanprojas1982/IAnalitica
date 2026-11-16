namespace IAnalitica.Application.Facturacion;

// DTO de la cuenta de cobro para el PDF y el correo. Solo datos comerciales: nunca usuarios, conversaciones, textos de IA ni datos de pacientes (08 §33).

public sealed record EmisorDoc(string RazonSocial, string Nit, string InstruccionesPago);
public sealed record ClienteDoc(string RazonSocial, string Nit, string? Dv, string? Direccion);
public sealed record LineaDoc(string Concepto, int Dias, int DiasPeriodo, decimal ValorCop);
public sealed record DetalleIaDoc(string Modelo, int Llamadas, long TokensEntrada, long TokensSalida, decimal ValorVentaCop);

public sealed record DocumentoCuentaCobro(EmisorDoc Emisor, ClienteDoc Cliente, string Numero, DateOnly Emision, DateOnly Vence, int PeriodoKey,
    IReadOnlyList<LineaDoc> Lineas, IReadOnlyList<DetalleIaDoc> DetalleIa, decimal Total, decimal SaldoAnteriorPendiente, string InstruccionesPago, bool Anulada);

/// <summary>Genera el PDF en memoria desde el DTO (puro: solo puede mostrar lo que el DTO trae).</summary>
public interface IGeneradorPdfCuentaCobro
{
    byte[] Generar(DocumentoCuentaCobro doc);
}

/// <summary>Almacén de los PDF emitidos: separado del de reportes porque no caduca a los 7 días (08 §34, retención de años).</summary>
public interface IAlmacenDocumentosFacturacion
{
    Task<string> GuardarAsync(int empresaId, string nombre, byte[] contenido, CancellationToken ct);
    Task<Stream?> AbrirAsync(string ruta, CancellationToken ct);
}
