using System.Net;
using System.Text.RegularExpressions;
using MatHub.Services;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatHub.Controls;

/// <summary>
/// The one code base of a row of numbers: <c>&lt;mh-stats&gt;&lt;mh-stat icon="monitor" label="Clients" value="8" hint="3 online" href="/clients" tone="ok" /&gt;&lt;/mh-stats&gt;</c>.
/// The row is a grid (four in a row on a wide screen, two on a phone); a tile with <c>href</c> is a link as a whole; the tone (ok, warn, danger, info) colours its
/// icon and is never the only thing that tells it (the label says what the number is).
/// </summary>
[HtmlTargetElement("mh-stats")]
public sealed class StatsTagHelper : TagHelper
{
    [HtmlAttributeName("label")] public string? Label { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.Attributes.SetAttribute("class", "mh-stats");
        if (!string.IsNullOrEmpty(Label))
        {
            output.Attributes.SetAttribute("role", "group");
            output.Attributes.SetAttribute("aria-label", Label);
        }
    }
}

[HtmlTargetElement("mh-stat", TagStructure = TagStructure.WithoutEndTag)]
public sealed partial class StatTagHelper : TagHelper
{
    [HtmlAttributeName("icon")] public string Icon { get; set; } = "info";

    [HtmlAttributeName("label")] public string Label { get; set; } = string.Empty;

    [HtmlAttributeName("value")] public string Value { get; set; } = string.Empty;

    [HtmlAttributeName("hint")] public string? Hint { get; set; }

    [HtmlAttributeName("href")] public string? Href { get; set; }

    /// <summary>ok, warn, danger, info or nothing (the accent).</summary>
    [HtmlAttributeName("tone")] public string? Tone { get; set; }

    /// <summary>A long text instead of a number (an operating system, a processor): smaller, may wrap.</summary>
    [HtmlAttributeName("text")] public bool IsText { get; set; }

    [GeneratedRegex("^(ok|warn|danger|info)$")]
    private static partial Regex ToneRegex();

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var link = !string.IsNullOrEmpty(Href);
        output.TagName = link ? "a" : "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        var cls = "mat-card mh-stat" + (link ? " mat-card--link" : string.Empty) + (Tone is not null && ToneRegex().IsMatch(Tone) ? $" mh-stat--{Tone}" : string.Empty);
        output.Attributes.SetAttribute("class", cls);
        if (link)
        {
            output.Attributes.SetAttribute("href", Href);
        }

        var hint = string.IsNullOrEmpty(Hint) ? string.Empty : $"<span class=\"mh-stat__hint\">{WebUtility.HtmlEncode(Hint)}</span>";
        var valueClass = IsText ? "mh-stat__value mh-stat__value--text" : "mh-stat__value";
        output.Content.SetHtmlContent(
            $"<span class=\"mh-stat__icon\">{Ui.Icon(Icon)}</span>" +
            $"<span class=\"mh-stat__body\"><span class=\"mh-stat__label\">{WebUtility.HtmlEncode(Label)}</span>" +
            $"<span class=\"{valueClass}\">{WebUtility.HtmlEncode(Value)}</span>{hint}</span>");
    }
}
