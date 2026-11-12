using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Packages;

namespace MatHub.Pages.Packages;

/// <summary>
/// The fields of an installer in the assistant and on the tab Versions: the version, the platform, the kind and what the kind needs. A file is first staged and
/// read (<see cref="InstallerAnalysis"/>): what MatHub finds fills the fields, the person checks it and saves.
/// </summary>
public sealed class InstallerInput
{
    public string Version { get; set; } = string.Empty;

    public InstallerArchitecture Architecture { get; set; } = InstallerArchitecture.X64;

    public PackageKind Kind { get; set; } = PackageKind.Msi;

    public string Arguments { get; set; } = string.Empty;

    public string UninstallCommand { get; set; } = string.Empty;

    public string ProductCode { get; set; } = string.Empty;

    public string WingetId { get; set; } = string.Empty;

    public string WingetVersion { get; set; } = string.Empty;

    public string InstallScript { get; set; } = string.Empty;

    public string UninstallScript { get; set; } = string.Empty;

    /// <summary>The staged file (a token of <see cref="PackageFiles.StageAsync"/>).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>What the analysis found, for the person.</summary>
    public string? Found { get; set; }

    public static List<Choice> Platforms { get; } = Enum.GetValues<InstallerArchitecture>()
        .Select(a => new Choice(a.ToString(), Text.T("Versions.Platform." + PackageVersions.Code(a)), Text.T("Versions.Platform." + PackageVersions.Code(a) + ".Hint"))).ToList();

    public static List<Choice> Kinds { get; } = Enum.GetValues<PackageKind>()
        .Select(k => new Choice(k.ToString(), Text.T("Packages.Kind." + k), Text.T("Packages.Kind." + k + ".Hint"))).ToList();

    public InstallerForm Form() => Kind switch
    {
        PackageKind.Script => new InstallerForm(Architecture, Kind, InstallScript: InstallScript, UninstallCommand: UninstallScript),
        PackageKind.Winget => new InstallerForm(Architecture, Kind, WingetId: WingetId, WingetVersion: string.IsNullOrWhiteSpace(WingetVersion) ? Version : WingetVersion),
        _ => new InstallerForm(Architecture, Kind, Arguments, Kind == PackageKind.Exe ? UninstallCommand : null, ProductCode: ProductCode),
    };

    /// <summary>Fills the fields from what the analysis found (only those a person left empty, the platform and the kind always).</summary>
    public void Take(InstallerFacts facts, string fileName)
    {
        Kind = facts.Kind;
        Architecture = facts.Architecture;
        if (string.IsNullOrWhiteSpace(Version) && facts.Version is not null)
        {
            Version = facts.Version;
        }

        if (string.IsNullOrWhiteSpace(Arguments) && facts.Arguments is not null)
        {
            Arguments = facts.Arguments;
        }

        if (string.IsNullOrWhiteSpace(ProductCode) && facts.ProductCode is not null)
        {
            ProductCode = facts.ProductCode;
        }

        var parts = new List<string> { fileName };
        if (facts.ProductName is not null)
        {
            parts.Add(facts.ProductName);
        }

        if (facts.Version is not null)
        {
            parts.Add(Text.T("Store.Version", facts.Version));
        }

        parts.Add(Text.T("Versions.Platform." + PackageVersions.Code(facts.Architecture)));
        parts.Add(facts.Framework is { } f ? Text.T("Versions.Framework", f) : Text.T("Versions.Framework.Unknown"));
        Found = string.Join(" · ", parts);
    }
}
