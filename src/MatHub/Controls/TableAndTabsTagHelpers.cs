using System.Net;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatHub.Controls;

/// <summary>
/// The one code base of a table (TABLE-01 .. TABLE-04): <c>&lt;mh-table caption="Notes"&gt;&lt;thead&gt;...&lt;/thead&gt;&lt;tbody&gt;...&lt;/tbody&gt;&lt;/mh-table&gt;</c>.
/// It draws the wrapper that scrolls sideways and the caption that screen readers read; every <c>td</c> carries <c>data-label</c> (on a phone a row becomes a card).
/// </summary>
[HtmlTargetElement("mh-table")]
public sealed class TableTagHelper : TagHelper
{
    [HtmlAttributeName("caption")] public string Caption { get; set; } = string.Empty;

    [HtmlAttributeName("zebra")] public bool Zebra { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.Attributes.SetAttribute("class", "mat-table-wrap");
        output.PreContent.SetHtmlContent($"<table class=\"mat-table{(Zebra ? " mat-table--zebra" : string.Empty)}\"><caption class=\"mat-sr-only\">{WebUtility.HtmlEncode(Caption)}</caption>");
        output.PostContent.SetHtmlContent("</table>");
    }
}

/// <summary>
/// The one code base of a tab bar whose tabs are pages that belong together: <c>&lt;mh-tabs label="Account"&gt;&lt;mh-tab href="/Account" active&gt;Profile&lt;/mh-tab&gt;&lt;/mh-tabs&gt;</c>.
/// Several tab bars can stand on one page.
/// </summary>
[HtmlTargetElement("mh-tabs")]
public sealed class TabsTagHelper : TagHelper
{
    [HtmlAttributeName("label")] public string Label { get; set; } = Localization.Text.T("Common.Sections");

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "nav";
        output.Attributes.SetAttribute("class", "mat-tabs");
        output.Attributes.SetAttribute("aria-label", Label);
    }
}

[HtmlTargetElement("mh-tab", ParentTag = "mh-tabs")]
public sealed class TabTagHelper : TagHelper
{
    [HtmlAttributeName("href")] public string Href { get; set; } = "#";

    [HtmlAttributeName("active")] public bool Active { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "a";
        output.Attributes.SetAttribute("class", Active ? "mat-tab is-active" : "mat-tab");
        output.Attributes.SetAttribute("href", Href);
        if (Active)
        {
            output.Attributes.SetAttribute("aria-current", "page");
        }
    }
}
