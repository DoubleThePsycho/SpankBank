using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace SpankBank.Web.Services;

/// <summary>Renders an inline lucide icon, like e621ng's svg_icon helper: &lt;svg-icon name="search" /&gt;.</summary>
[HtmlTargetElement("svg-icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class SvgIconTagHelper : TagHelper
{
    public string Name { get; set; } = "";
    public int Width { get; set; } = 24;
    public int Height { get; set; } = 24;
    public double StrokeWidth { get; set; } = 2;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var name = Name.Replace('-', '_');
        IconPaths.Paths.TryGetValue(name, out var paths);

        var cssClass = output.Attributes["class"]?.Value?.ToString();
        output.TagName = "svg";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.Clear();
        output.Attributes.SetAttribute("xmlns", "http://www.w3.org/2000/svg");
        output.Attributes.SetAttribute("width", Width);
        output.Attributes.SetAttribute("height", Height);
        output.Attributes.SetAttribute("viewBox", "0 0 24 24");
        output.Attributes.SetAttribute("fill", "none");
        output.Attributes.SetAttribute("stroke", "currentColor");
        output.Attributes.SetAttribute("stroke-width", StrokeWidth);
        output.Attributes.SetAttribute("stroke-linecap", "round");
        output.Attributes.SetAttribute("stroke-linejoin", "round");
        output.Attributes.SetAttribute("name", name);
        if (!string.IsNullOrEmpty(cssClass))
            output.Attributes.SetAttribute("class", cssClass);
        output.Content.SetHtmlContent(new HtmlString(paths ?? ""));
    }
}
