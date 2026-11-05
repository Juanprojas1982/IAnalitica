using Microsoft.AspNetCore.Components;

namespace IAnalitica.Web.Components.Dashboards;

/// <summary>El elemento SVG &lt;text&gt; choca con la etiqueta especial &lt;text&gt; de Razor dentro de bloques de código: se emite con el constructor de árbol.</summary>
public static class Svg
{
    public static RenderFragment Texto(string x, string y, string anchor, string clase, string contenido) => b =>
    {
        b.OpenElement(0, "text");
        b.AddAttribute(1, "x", x);
        b.AddAttribute(2, "y", y);
        b.AddAttribute(3, "text-anchor", anchor);
        b.AddAttribute(4, "class", clase);
        b.AddContent(5, contenido);
        b.CloseElement();
    };
}
