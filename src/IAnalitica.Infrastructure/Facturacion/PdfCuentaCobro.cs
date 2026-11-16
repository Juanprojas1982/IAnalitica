using System.Globalization;
using IAnalitica.Application.Facturacion;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace IAnalitica.Infrastructure.Facturacion;

/// <summary>
/// PDF de la cuenta de cobro (QuestPDF, licencia Community como <c>ExportadorPdf</c>, D33). Recibe solo el DTO, así que no puede incluir nada que el DTO no traiga.
/// Lleva una leyenda fija de documento no tributario: no es una factura electrónica (08 §34).
/// </summary>
internal sealed class PdfCuentaCobro : IGeneradorPdfCuentaCobro
{
    static PdfCuentaCobro() => QuestPDF.Settings.License = LicenseType.Community;

    private static readonly CultureInfo Co = new("es-CO");
    private static readonly string[] Meses = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];
    private const string Color = "#0B3D91";

    public byte[] Generar(DocumentoCuentaCobro d)
    {
        var periodo = $"{Meses[d.PeriodoKey % 100 - 1]} de {d.PeriodoKey / 100}";
        return Document.Create(doc => doc.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.Margin(36);
            p.DefaultTextStyle(t => t.FontSize(10));
            p.Header().Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem().Column(e =>
                    {
                        e.Item().Text(d.Emisor.RazonSocial).FontSize(13).Bold();
                        e.Item().Text($"NIT {d.Emisor.Nit}").FontColor(Colors.Grey.Darken2);
                    });
                    r.ConstantItem(190).AlignRight().Column(e =>
                    {
                        e.Item().Text(d.Anulada ? "CUENTA DE COBRO — ANULADA" : "CUENTA DE COBRO").FontSize(13).Bold().FontColor(d.Anulada ? Colors.Red.Darken2 : Color);
                        e.Item().Text(d.Numero).FontSize(16).Bold();
                    });
                });
                c.Item().PaddingTop(6).LineHorizontal(1).LineColor(Color);
            });
            p.Content().PaddingVertical(12).Column(col =>
            {
                col.Spacing(10);
                col.Item().Row(r =>
                {
                    r.RelativeItem().Column(e =>
                    {
                        e.Item().Text("Cliente").Bold().FontColor(Color);
                        e.Item().Text(d.Cliente.RazonSocial);
                        e.Item().Text($"NIT {d.Cliente.Nit}{(string.IsNullOrWhiteSpace(d.Cliente.Dv) ? "" : "-" + d.Cliente.Dv)}");
                        if (!string.IsNullOrWhiteSpace(d.Cliente.Direccion)) e.Item().Text(d.Cliente.Direccion);
                    });
                    r.RelativeItem().Column(e =>
                    {
                        e.Item().Text("Fechas").Bold().FontColor(Color);
                        e.Item().Text($"Periodo de servicio: {periodo}");
                        e.Item().Text($"Emisión: {d.Emision:dd/MM/yyyy}");
                        e.Item().Text($"Vencimiento: {d.Vence:dd/MM/yyyy}").Bold();
                    });
                });

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(); c.ConstantColumn(70); c.ConstantColumn(100); });
                    t.Header(h =>
                    {
                        h.Cell().Background(Color).Padding(4).Text("Concepto").FontColor(Colors.White).Bold();
                        h.Cell().Background(Color).Padding(4).AlignRight().Text("Días").FontColor(Colors.White).Bold();
                        h.Cell().Background(Color).Padding(4).AlignRight().Text("Valor (COP)").FontColor(Colors.White).Bold();
                    });
                    foreach (var l in d.Lineas)
                    {
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(l.Concepto);
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(l.Dias == l.DiasPeriodo ? "mes completo" : $"{l.Dias}/{l.DiasPeriodo}");
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(Moneda(l.ValorCop));
                    }
                });

                col.Item().AlignRight().Column(e =>
                {
                    e.Item().Text($"TOTAL A PAGAR: {Moneda(d.Total)}").FontSize(13).Bold().FontColor(Color);
                    if (d.SaldoAnteriorPendiente > 0)
                        e.Item().Text($"Saldo pendiente de cuentas anteriores: {Moneda(d.SaldoAnteriorPendiente)}").FontSize(9).FontColor(Colors.Grey.Darken2);
                });

                if (d.DetalleIa.Count > 0)
                    col.Item().Column(e =>
                    {
                        e.Item().Text("Detalle del consumo de IA (agregado por modelo)").Bold().FontColor(Color);
                        foreach (var x in d.DetalleIa)
                            e.Item().Text($"{x.Modelo}: {x.Llamadas.ToString("N0", Co)} llamadas · {x.TokensEntrada.ToString("N0", Co)} tokens de entrada · {x.TokensSalida.ToString("N0", Co)} de salida · {Moneda(x.ValorVentaCop)}").FontSize(9);
                    });

                col.Item().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(e =>
                {
                    e.Item().Text("Instrucciones de pago").Bold().FontColor(Color);
                    e.Item().Text(d.InstruccionesPago);
                    e.Item().PaddingTop(4).Text("Nunca cambiamos datos bancarios por correo. Ante cualquier duda, confirme por un canal distinto al correo.").FontSize(8).FontColor(Colors.Grey.Darken2);
                });
            });
            p.Footer().Column(f =>
            {
                f.Item().Text("Cuenta de cobro: documento no tributario; no constituye factura electrónica de venta.").FontSize(8).FontColor(Colors.Grey.Darken2);
                f.Item().Text(t => { t.DefaultTextStyle(x => x.FontSize(7).FontColor(Colors.Grey.Darken1)); t.Span($"{d.Numero} · Página "); t.CurrentPageNumber(); t.Span(" de "); t.TotalPages(); });
            });
        })).GeneratePdf();
    }

    private static string Moneda(decimal v) => "$ " + v.ToString("N0", Co);
}
