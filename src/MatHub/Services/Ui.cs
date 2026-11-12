using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace MatHub.Services;

/// <summary>Small helpers for the markup of the toolkit: icons, the logo, initials, sizes.</summary>
public static partial class Ui
{
    public const string AppId = "mathub";
    public const string AppName = "MatHub";

    /// <summary>The accent of MatHub: amber (white text on it 5.0:1).</summary>
    public const string Accent = "#b45309";

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColourRegex();

    /// <summary>An icon from the page's inline sprite (the toolkit's and the app's own).</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null, string? colour = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        var data = IsColour(colour) ? $" data-colour=\"{colour}\"" : string.Empty;
        return new HtmlString($"<svg class=\"{cls}\"{data} aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    /// <summary>A moment as a person reads it in a list: "5 min ago", "Yesterday 14:05" … with the exact time as its title.</summary>
    public static IHtmlContent When(DateTime? utc, string? zone, string? none = null)
    {
        if (utc is null)
        {
            return new HtmlString($"<span class=\"mh-muted\">{System.Net.WebUtility.HtmlEncode(none ?? "–")}</span>");
        }

        var iso = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var stamp = System.Net.WebUtility.HtmlEncode(Dates.Stamp(utc, zone));
        var ago = System.Net.WebUtility.HtmlEncode(Dates.Ago(utc.Value, zone));
        return new HtmlString($"<time class=\"mh-when\" datetime=\"{iso}\" title=\"{stamp}\">{ago}</time>");
    }

    /// <summary>An operating system as it is read in a list: "Windows 11 Pro" (without the "Microsoft" every Windows starts with).</summary>
    public static string Os(string? name)
        => string.IsNullOrWhiteSpace(name) ? "–" : name.Replace("Microsoft ", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>"1 client", "3 clients".</summary>
    public static string ClientCount(int n) => Localization.Text.T(n == 1 ? "Jobs.ClientCount.One" : "Jobs.ClientCount", n);

    public static bool IsColour(string? value) => value is not null && ColourRegex().IsMatch(value);

    /// <summary>The logo of the menu and the sign-in: the mark of MatHub.</summary>
    public static IHtmlContent Logo(string size = "md")
        => new HtmlString($"<span class=\"mat-logo mat-logo--{size} mh-logo\" role=\"img\" aria-label=\"{AppName}\"><svg class=\"mat-icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-mathub\"></use></svg></span>");

    public static string Initials(string name)
    {
        var letters = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return letters.Length == 0 ? "?" : letters;
    }

    /// <summary>A size for people: 812 B, 14.2 KB, 3.1 MB.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var culture = CultureInfo.CurrentUICulture;
        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.0" : "0", culture) + " " + units[unit];
    }
}
