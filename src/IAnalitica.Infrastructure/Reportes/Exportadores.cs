using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using IAnalitica.Application.Reportes;
using IAnalitica.Domain.Modulos;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace IAnalitica.Infrastructure.Reportes;

internal static class Texto
{
    /// <summary>Quita los caracteres de control que XML y los lectores de CSV no admiten.</summary>
    public static string Limpiar(string? s)
        => s is null ? "" : new string(s.Select(c => c < 0x20 && c is not ('\t' or '\n' or '\r') ? ' ' : c).ToArray());

    public static string Decimal(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>CSV (RFC 4180, UTF-8 con BOM para Excel): números con punto decimal, sin símbolos; texto que empiece por = + - @ se neutraliza (inyección de fórmulas).</summary>
internal sealed class ExportadorCsv : IExportadorReporte
{
    public string Formato => FormatosReporte.Csv;
    public string TipoContenido => "text/csv; charset=utf-8";
    public string Extension => "csv";

    public byte[] Exportar(ReporteDashboard r)
    {
        var sb = new StringBuilder();
        Linea(sb, "Reporte", r.Titulo);
        Linea(sb, "Empresa", r.Empresa);
        foreach (var (n, v) in r.Parametros) Linea(sb, n, v);
        Linea(sb, "Datos a la carga", r.VersionCarga.ToString(CultureInfo.InvariantCulture));
        Linea(sb, "Generado (UTC)", r.GeneradoEnUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        foreach (var s in r.Secciones)
        {
            sb.Append("\r\n");
            Linea(sb, "# " + s.Titulo);
            if (s.Nota is not null) Linea(sb, "# " + s.Nota);
            Linea(sb, s.Columnas.Select(c => c.Nombre + (c.EsNumero && c.Unidad != "" ? $" ({c.Unidad})" : "")).ToArray());
            foreach (var fila in s.Filas)
                Linea(sb, fila.Select((v, i) => v switch
                {
                    null => "",
                    decimal d => Texto.Decimal(d),
                    _ => Neutralizar(v.ToString() ?? ""),
                }).ToArray());
        }
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Neutralizar(string s) => s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + s : s;

    private static void Linea(StringBuilder sb, params string[] campos)
    {
        sb.Append(string.Join(',', campos.Select(Campo))).Append("\r\n");
    }

    private static string Campo(string s)
    {
        s = Texto.Limpiar(s);
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>
/// Excel (.xlsx) escrito a mano sobre OpenXML (ZIP + XML): sin librerías ni licencias. Una hoja «Resumen» con los parámetros y una hoja
/// por widget; los números son números (con formato por unidad), no texto.
/// </summary>
internal sealed class ExportadorXlsx : IExportadorReporte
{
    public string Formato => FormatosReporte.Xlsx;
    public string TipoContenido => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public string Extension => "xlsx";

    // Estilos (cellXfs): 0 normal · 1 encabezado · 2 enteros #,##0 · 3 un decimal 0.0 · 4 dos decimales #,##0.00 · 5 título
    private static int Estilo(string unidad) => unidad switch { "COP" or "unidades" => 2, "%" or "dias" => 3, "veces" => 4, _ => 2 };

    public byte[] Exportar(ReporteDashboard r)
    {
        var hojas = new List<(string Nombre, Action<XmlWriter> Escribir, bool Congelar)>();
        hojas.Add(("Resumen", w => Resumen(w, r), false));
        var usados = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Resumen" };
        foreach (var s in r.Secciones) hojas.Add((NombreHoja(s.Titulo, usados), w => Datos(w, s), true));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Parte(zip, "[Content_Types].xml", w =>
            {
                w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Elemento(w, "Default", ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
                Elemento(w, "Default", ("Extension", "xml"), ("ContentType", "application/xml"));
                Elemento(w, "Override", ("PartName", "/xl/workbook.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
                Elemento(w, "Override", ("PartName", "/xl/styles.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
                for (var i = 0; i < hojas.Count; i++)
                    Elemento(w, "Override", ("PartName", $"/xl/worksheets/sheet{i + 1}.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
                w.WriteEndElement();
            });
            Parte(zip, "_rels/.rels", w =>
            {
                w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                Elemento(w, "Relationship", ("Id", "rId1"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), ("Target", "xl/workbook.xml"));
                w.WriteEndElement();
            });
            Parte(zip, "xl/workbook.xml", w =>
            {
                w.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                w.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                w.WriteStartElement("sheets");
                for (var i = 0; i < hojas.Count; i++)
                {
                    w.WriteStartElement("sheet");
                    w.WriteAttributeString("name", hojas[i].Nombre);
                    w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", $"rId{i + 1}");
                    w.WriteEndElement();
                }
                w.WriteEndElement(); w.WriteEndElement();
            });
            Parte(zip, "xl/_rels/workbook.xml.rels", w =>
            {
                w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                for (var i = 0; i < hojas.Count; i++)
                    Elemento(w, "Relationship", ("Id", $"rId{i + 1}"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), ("Target", $"worksheets/sheet{i + 1}.xml"));
                Elemento(w, "Relationship", ("Id", $"rId{hojas.Count + 1}"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"), ("Target", "styles.xml"));
                w.WriteEndElement();
            });
            Parte(zip, "xl/styles.xml", Estilos);
            for (var i = 0; i < hojas.Count; i++) Parte(zip, $"xl/worksheets/sheet{i + 1}.xml", hojas[i].Escribir);
        }
        return ms.ToArray();
    }

    private static void Estilos(XmlWriter w)
    {
        w.WriteStartElement("styleSheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        w.WriteStartElement("numFmts"); w.WriteAttributeString("count", "1");
        Elemento(w, "numFmt", ("numFmtId", "164"), ("formatCode", "0.0"));
        w.WriteEndElement();
        w.WriteStartElement("fonts"); w.WriteAttributeString("count", "3");
        w.WriteRaw("<font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><name val=\"Calibri\"/></font>");
        w.WriteEndElement();
        w.WriteStartElement("fills"); w.WriteAttributeString("count", "3");
        w.WriteRaw("<fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE8EDF3\"/></patternFill></fill>");
        w.WriteEndElement();
        w.WriteStartElement("borders"); w.WriteAttributeString("count", "1");
        w.WriteRaw("<border><left/><right/><top/><bottom/><diagonal/></border>");
        w.WriteEndElement();
        w.WriteStartElement("cellStyleXfs"); w.WriteAttributeString("count", "1");
        w.WriteRaw("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>");
        w.WriteEndElement();
        w.WriteStartElement("cellXfs"); w.WriteAttributeString("count", "6");
        w.WriteRaw("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                 + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
                 + "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
                 + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
                 + "<xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
                 + "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>");
        w.WriteEndElement();
        w.WriteStartElement("cellStyles"); w.WriteAttributeString("count", "1");
        w.WriteRaw("<cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/>");
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Resumen(XmlWriter w, ReporteDashboard r)
    {
        w.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        w.WriteStartElement("cols");
        Elemento(w, "col", ("min", "1"), ("max", "1"), ("width", "26"), ("customWidth", "1"));
        Elemento(w, "col", ("min", "2"), ("max", "2"), ("width", "60"), ("customWidth", "1"));
        w.WriteEndElement();
        w.WriteStartElement("sheetData");
        var fila = 1;
        Fila(w, fila++, new object?[] { r.Titulo }, 5);
        Fila(w, fila++, new object?[] { "Empresa", r.Empresa });
        Fila(w, fila++, new object?[] { "Módulo", r.Modulo });
        foreach (var (n, v) in r.Parametros) Fila(w, fila++, new object?[] { n, v });
        Fila(w, fila++, new object?[] { "Datos a la carga", (decimal)r.VersionCarga }, estilosNumero: new[] { 0, 2 });
        Fila(w, fila++, new object?[] { "Generado (UTC)", r.GeneradoEnUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) });
        fila++;
        Fila(w, fila++, new object?[] { "Hojas" }, 1);
        foreach (var s in r.Secciones) Fila(w, fila++, new object?[] { s.Titulo, s.Nota });
        w.WriteEndElement(); w.WriteEndElement();
    }

    private static void Datos(XmlWriter w, SeccionReporte s)
    {
        w.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        w.WriteStartElement("sheetViews"); w.WriteStartElement("sheetView"); w.WriteAttributeString("workbookViewId", "0");
        Elemento(w, "pane", ("ySplit", "1"), ("topLeftCell", "A2"), ("activePane", "bottomLeft"), ("state", "frozen"));
        w.WriteEndElement(); w.WriteEndElement();
        w.WriteStartElement("cols");
        for (var c = 0; c < s.Columnas.Count; c++)
        {
            var ancho = Math.Clamp(Math.Max(s.Columnas[c].Nombre.Length, s.Filas.Take(200).Select(f => (f[c]?.ToString() ?? "").Length).DefaultIfEmpty(0).Max()) + 3, 10, 60);
            Elemento(w, "col", ("min", (c + 1).ToString(CultureInfo.InvariantCulture)), ("max", (c + 1).ToString(CultureInfo.InvariantCulture)), ("width", ancho.ToString(CultureInfo.InvariantCulture)), ("customWidth", "1"));
        }
        w.WriteEndElement();
        w.WriteStartElement("sheetData");
        Fila(w, 1, s.Columnas.Select(c => (object?)(c.Nombre + (c.EsNumero && c.Unidad != "" ? $" ({c.Unidad})" : ""))).ToArray(), 1);
        var estilos = s.Columnas.Select(c => c.EsNumero ? Estilo(c.Unidad) : 0).ToArray();
        for (var i = 0; i < s.Filas.Count; i++) Fila(w, i + 2, s.Filas[i].ToArray(), estilosNumero: estilos);
        w.WriteEndElement(); w.WriteEndElement();
    }

    private static void Fila(XmlWriter w, int numero, object?[] celdas, int? estiloTodas = null, int[]? estilosNumero = null)
    {
        w.WriteStartElement("row"); w.WriteAttributeString("r", numero.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < celdas.Length; i++)
        {
            if (celdas[i] is null) continue;
            var referencia = Columna(i) + numero.ToString(CultureInfo.InvariantCulture);
            w.WriteStartElement("c"); w.WriteAttributeString("r", referencia);
            if (celdas[i] is decimal d)
            {
                var estilo = estilosNumero is not null && i < estilosNumero.Length ? estilosNumero[i] : 2;
                if (estilo != 0) w.WriteAttributeString("s", estilo.ToString(CultureInfo.InvariantCulture));
                w.WriteElementString("v", Texto.Decimal(d));
            }
            else
            {
                var estilo = estiloTodas ?? 0;
                if (estilo != 0) w.WriteAttributeString("s", estilo.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("t", "inlineStr");
                w.WriteStartElement("is");
                w.WriteStartElement("t"); w.WriteAttributeString("xml", "space", null, "preserve"); w.WriteString(Texto.Limpiar(celdas[i]?.ToString())); w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static string Columna(int indice)
    {
        var sb = new StringBuilder();
        for (var n = indice; n >= 0; n = n / 26 - 1) sb.Insert(0, (char)('A' + n % 26));
        return sb.ToString();
    }

    /// <summary>Nombre de hoja válido: máximo 31 caracteres, sin : \ / ? * [ ], único sin importar mayúsculas.</summary>
    private static string NombreHoja(string titulo, HashSet<string> usados)
    {
        var limpio = new string(titulo.Where(c => !":\\/?*[]".Contains(c) && c >= ' ').ToArray()).Trim().Trim('\'');
        if (limpio.Length == 0) limpio = "Hoja";
        var nombre = limpio.Length > 31 ? limpio[..31].TrimEnd() : limpio;
        for (var n = 2; !usados.Add(nombre); n++)
        {
            var sufijo = " (" + n + ")";
            nombre = (limpio.Length + sufijo.Length > 31 ? limpio[..(31 - sufijo.Length)].TrimEnd() : limpio) + sufijo;
        }
        return nombre;
    }

    private static void Parte(ZipArchive zip, string ruta, Action<XmlWriter> escribir)
    {
        var entrada = zip.CreateEntry(ruta, CompressionLevel.Optimal);
        using var flujo = entrada.Open();
        using var w = XmlWriter.Create(flujo, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = true });
        w.WriteStartDocument(true);
        escribir(w);
        w.WriteEndDocument();
    }

    private static void Elemento(XmlWriter w, string nombre, params (string Atributo, string Valor)[] atributos)
    {
        w.WriteStartElement(nombre);
        foreach (var (a, v) in atributos) w.WriteAttributeString(a, v);
        w.WriteEndElement();
    }
}

/// <summary>
/// PDF con QuestPDF (licencia comunitaria, D33: la empresa factura menos de USD 1 M al año; el dueño debe re-certificarlo al cierre de cada
/// ejercicio). Es un resumen gerencial: encabezado de marca, parámetros y tablas; el detalle completo está en Excel o CSV.
/// </summary>
internal sealed class ExportadorPdf : IExportadorReporte
{
    public const int MaxFilasPorSeccion = 500;

    static ExportadorPdf() => QuestPDF.Settings.License = LicenseType.Community;

    public string Formato => FormatosReporte.Pdf;
    public string TipoContenido => "application/pdf";
    public string Extension => "pdf";

    public string ColorPrimario { get; set; } = "#0B3D91";

    public byte[] Exportar(ReporteDashboard r)
    {
        var color = Regex.IsMatch(r.ColorPrimario ?? "", "^#[0-9A-Fa-f]{6}$") ? r.ColorPrimario! : ColorPrimario;
        return Document.Create(doc =>
        {
            doc.Page(p =>
            {
                p.Size(PageSizes.A4.Landscape());
                p.Margin(28);
                p.DefaultTextStyle(t => t.FontSize(9));
                p.Header().Column(c =>
                {
                    c.Item().Text(r.Empresa).FontSize(10).FontColor(Colors.Grey.Darken2);
                    c.Item().Text(r.Titulo).FontSize(18).Bold().FontColor(color);
                    c.Item().PaddingBottom(6).Text(string.Join("  ·  ", r.Parametros.Select(x => $"{x.Nombre}: {x.Valor}"))).FontSize(8).FontColor(Colors.Grey.Darken2);
                    c.Item().LineHorizontal(1).LineColor(color);
                });
                p.Content().PaddingVertical(8).Column(col =>
                {
                    col.Spacing(10);
                    foreach (var s in r.Secciones) col.Item().Element(e => Seccion(e, s, color));
                });
                p.Footer().Row(f =>
                {
                    f.RelativeItem().Text($"Generado por IAnalitica · datos a la carga {r.VersionCarga} · {r.GeneradoEnUtc:yyyy-MM-dd HH:mm} UTC").FontSize(7).FontColor(Colors.Grey.Darken1);
                    f.AutoItem().Text(t => { t.DefaultTextStyle(x => x.FontSize(7).FontColor(Colors.Grey.Darken1)); t.Span("Página "); t.CurrentPageNumber(); t.Span(" de "); t.TotalPages(); });
                });
            });
        }).GeneratePdf();
    }

    private static void Seccion(IContainer e, SeccionReporte s, string color)
    {
        e.Column(c =>
        {
            c.Item().PaddingBottom(3).Text(s.Titulo).FontSize(11).Bold().FontColor(color);
            if (s.Filas.Count == 0) { c.Item().Text("Sin datos para los filtros elegidos.").FontColor(Colors.Grey.Darken1); return; }
            var filas = s.Filas.Take(MaxFilasPorSeccion).ToList();
            c.Item().Table(t =>
            {
                t.ColumnsDefinition(cols => { foreach (var col in s.Columnas) { if (col.EsNumero) cols.RelativeColumn(1); else cols.RelativeColumn(2); } });
                t.Header(h =>
                {
                    foreach (var col in s.Columnas)
                    {
                        var celda = h.Cell().Background(Colors.Grey.Lighten3).BorderBottom(1).BorderColor(Colors.Grey.Medium).Padding(3);
                        (col.EsNumero ? celda.AlignRight() : celda).Text(col.Nombre).Bold().FontSize(8);
                    }
                });
                foreach (var fila in filas)
                    for (var i = 0; i < s.Columnas.Count; i++)
                    {
                        var celda = t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);
                        var texto = s.Columnas[i].EsNumero ? Formateo(fila[i] as decimal?, s.Columnas[i].Unidad) : Texto.Limpiar(fila[i]?.ToString());
                        (s.Columnas[i].EsNumero ? celda.AlignRight() : celda).Text(texto);
                    }
            });
            var nota = s.Filas.Count > MaxFilasPorSeccion ? $"Se muestran las primeras {MaxFilasPorSeccion} de {s.Filas.Count} filas; el detalle completo está en Excel o CSV." : s.Nota;
            if (nota is not null) c.Item().PaddingTop(2).Text(nota).FontSize(7).FontColor(Colors.Grey.Darken1);
        });
    }

    private static readonly NumberFormatInfo Co = new() { NumberGroupSeparator = ".", NumberDecimalSeparator = ",", NumberGroupSizes = new[] { 3 } };

    private static string Formateo(decimal? v, string unidad) => v is not { } d ? "—" : unidad switch
    {
        "COP" => (d < 0 ? "-" : "") + "$ " + Math.Abs(d).ToString("#,##0", Co),
        "%" => d.ToString("#,##0.0", Co) + " %",
        "dias" => d.ToString("#,##0.#", Co) + " d",
        "veces" => d.ToString("#,##0.00", Co),
        _ => d.ToString("#,##0", Co),
    };
}
