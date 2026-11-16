using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using MatDesktop;

namespace MatHub.Agent.Ui;

/// <summary>
/// The look of the windows of the agent. The colours are the tokens of MatUi (MatDesign DESK-02, AGENT-05): <c>MatDesktop/Tokens</c> is a copy of MatDesktop's
/// tokens (<c>node ../MatDesign/tools/matdesktop-copy.js src/MatHub.Agent --tokens-only</c>, never edit it), and <see cref="MatPalette.For"/> derives every
/// accent token from MatHub's accent the way the CSS does. WinForms has no transparency, so a token with an opacity is painted over the surface it stands on.
/// The fonts are those of Windows 11 (Segoe UI Variable, Segoe Fluent Icons) and their fallbacks.
/// </summary>
internal static class Theme
{
    /// <summary>MatHub's accent (IDENT-01): amber, white text on it 5.0:1.</summary>
    public const string AccentHex = "#b45309";

    /// <summary>The colour tokens of the light mode for MatHub's accent.</summary>
    public static readonly MatPalette Palette = MatPalette.For(AccentHex, dark: false);

    /// <summary>Cards, dialogs, the header of a window (<c>--mat-surface</c>).</summary>
    public static readonly Color Surface = Solid("--mat-surface");

    /// <summary>The background of a window, its footer (<c>--mat-bg</c>).</summary>
    public static readonly Color Background = Solid("--mat-bg");

    /// <summary>A quiet surface: the hover of a secondary button (<c>--mat-surface-2</c>).</summary>
    public static readonly Color Surface2 = Solid("--mat-surface-2");

    /// <summary>A pressed button, the box of an empty state (<c>--mat-surface-3</c>).</summary>
    public static readonly Color Surface3 = Solid("--mat-surface-3");

    public static readonly Color Border = Solid("--mat-border");
    public static readonly Color Text = Solid("--mat-text");
    public static readonly Color Muted = Solid("--mat-text-muted");
    public static readonly Color Accent = Solid("--mat-accent");
    public static readonly Color AccentHover = Solid("--mat-accent-hover");
    public static readonly Color AccentActive = Solid("--mat-accent-active");
    public static readonly Color OnAccent = Solid("--mat-on-accent");

    /// <summary>A tint of the accent: what is selected (COLOR-08), the box of an icon.</summary>
    public static readonly Color AccentSoft = Solid("--mat-accent-soft");

    public static readonly Color AccentSoftStrong = Solid("--mat-accent-soft-strong");

    /// <summary>The accent as text on a surface (<c>--mat-accent-text</c>).</summary>
    public static readonly Color AccentText = Solid("--mat-accent-text");

    /// <summary>Text in the status colours (<c>--mat-success-text</c> …), on a surface.</summary>
    public static readonly Color Ok = Solid("--mat-success-text");
    public static readonly Color Warning = Solid("--mat-warning-text");
    public static readonly Color Danger = Solid("--mat-danger-text");

    /// <summary>The tints of the status colours (<c>--mat-warning-bg</c>, <c>--mat-danger-bg</c>).</summary>
    public static readonly Color WarningSoft = Solid("--mat-warning-bg");
    public static readonly Color DangerSoft = Solid("--mat-danger-bg");

    /// <summary>The line of a danger button: <c>color-mix(in srgb, var(--mat-danger) 45%, transparent)</c>, as MatUi and MatDesktop draw it.</summary>
    public static readonly Color DangerLine = From(MatColor.Transparent(Palette.Danger, 0.45).Over(Palette.Surface));

    /// <summary>The dot of a state on the tray icon (TRAY-02): the status colours themselves.</summary>
    public static Color Dot(TrayState state) => state switch
    {
        TrayState.Running => Solid("--mat-success"),
        TrayState.Waiting => Solid("--mat-warning"),
        TrayState.Stopped => Solid("--mat-danger"),
        _ => Muted,
    };

    /// <summary>A token as a colour without transparency: painted over the surface (WinForms has no alpha in most places).</summary>
    public static Color Solid(string token) => From(Palette[token].Over(Palette.Surface));

    private static Color From(MatRgba c) => Color.FromArgb(c.R8, c.G8, c.B8);

    private static readonly Lazy<HashSet<string>> Families = new(() =>
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    });

    public static string UiFamily => Families.Value.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    public static string DisplayFamily => Families.Value.Contains("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI Semibold";

    public static string IconFamily => Families.Value.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new(UiFamily, size, style);

    public static Font Display(float size) => new(DisplayFamily, size, Families.Value.Contains("Segoe UI Variable Display") ? FontStyle.Bold : FontStyle.Regular);

    public static Font Icons(float size) => new(IconFamily, size);

    /// <summary>A path or an address that is read character by character (the data folder in Info).</summary>
    public static Font Mono(float size) => new(Families.Value.Contains("Cascadia Mono") ? "Cascadia Mono" : "Consolas", size);

    /// <summary>The glyph of an icon of the kiosk (the names of MatHub's icon set) in Segoe Fluent Icons / MDL2 Assets.</summary>
    public static string Glyph(string? icon) => icon switch
    {
        "download" => "",
        "wrench" => "",
        "printer" => "",
        "network" => "",
        "wifi" => "",
        "shield" => "",
        "lock" => "",
        "key" => "",
        "user" => "",
        "users" => "",
        "terminal" => "",
        "refresh" => "",
        "monitor" => "",
        "folder" => "",
        "mail" => "",
        "globe" => "",
        "settings" => "",
        "rocket" => "",
        "broom" => "",
        "help-circle" => "",
        "star" => "",
        "search" => "",
        "play" => "",
        "check" => "",
        "error" => "",
        "warning" => "",
        "info" => "",
        "question" => "",
        "clock" => "",
        "offline" => "",
        _ => "",
    };

    /// <summary>A rectangle with round corners.</summary>
    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The round corners and the title bar of Windows 11 for a window (nothing on Windows 10).</summary>
    public static void Windows11(Form form)
    {
        try
        {
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int));
            var mica = 2; // DWMSBT_MAINWINDOW
            DwmSetWindowAttribute(form.Handle, 38, ref mica, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // an older Windows
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>
/// A flat button in the colours of the tokens: primary (the accent), secondary (the surface with a border), quiet (no border), selected (a tint of the accent,
/// COLOR-08: a tab that is chosen) or danger (the danger text with its line, BUTTON-01: the button of the uninstall).
/// </summary>
internal sealed class FlatButton : Button
{
    public enum Kinds
    {
        Primary,
        Secondary,
        Quiet,
        Selected,
        Danger,
    }

    public FlatButton(string text, Kinds kind = Kinds.Secondary, string? glyph = null)
    {
        Kind = kind;
        Glyph = glyph;
        Text = text;
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
        Font = Theme.Ui(9.5f, kind is Kinds.Primary or Kinds.Selected or Kinds.Danger ? FontStyle.Bold : FontStyle.Regular);
        // as wide as the text needs (in every size of the screen), at least a comfortable button
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(88, 34);
        Padding = new Padding(12, 0, 12, 0);
        UseVisualStyleBackColor = false;
        Apply();
    }

    public Kinds Kind { get; }

    public string? Glyph { get; }

    private void Apply()
    {
        FlatAppearance.BorderSize = Kind is Kinds.Secondary or Kinds.Danger ? 1 : 0;
        FlatAppearance.BorderColor = Kind == Kinds.Danger ? Theme.DangerLine : Theme.Border;
        (BackColor, ForeColor, FlatAppearance.MouseOverBackColor, FlatAppearance.MouseDownBackColor) = Kind switch
        {
            Kinds.Primary => (Theme.Accent, Theme.OnAccent, Theme.AccentHover, Theme.AccentActive),
            Kinds.Secondary => (Theme.Surface, Theme.Text, Theme.Surface2, Theme.Surface3),
            Kinds.Selected => (Theme.AccentSoft, Theme.AccentText, Theme.AccentSoftStrong, Theme.AccentSoftStrong),
            Kinds.Danger => (Theme.Surface, Theme.Danger, Theme.DangerSoft, Theme.DangerSoft),
            _ => (Color.Transparent, Theme.Text, Theme.Surface3, Theme.Surface3),
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Glyph is not null && Image is null)
        {
            // the glyph as a small picture left of the text, in the size of the screen
            var size = (int)Math.Round(16 * DeviceDpi / 96.0);
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            using (var font = new Font(Theme.IconFamily, size * 0.62f, GraphicsUnit.Pixel))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var brush = new SolidBrush(ForeColor);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(Glyph, font, brush, new RectangleF(0, 1, size, size), format);
            }

            Image = bitmap;
            TextImageRelation = TextImageRelation.ImageBeforeText;
            ImageAlign = ContentAlignment.MiddleCenter;
            TextAlign = ContentAlignment.MiddleCenter;
        }
    }
}

/// <summary>A panel with round corners and a border, on the background of its parent.</summary>
internal class Card : Panel
{
    public Card()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        Padding = new Padding(16);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 8;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColour { get; set; } = Theme.Border;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using var fill = new SolidBrush(BackColor);
        using var pen = new Pen(BorderColour);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>A square with a glyph on a soft background: the icon of a tile, a dialog, a state.</summary>
internal sealed class GlyphBox : Control
{
    public GlyphBox(string glyph, Color colour, Color background, int size = 44, float glyphSize = 17f)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Glyph = glyph;
        Colour = colour;
        Fill = background;
        GlyphSize = glyphSize;
        Size = new Size(size, size);
        BackColor = Color.Transparent;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Colour { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; }

    public float GlyphSize { get; }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(4, Width / 5)))
        using (var brush = new SolidBrush(Fill))
        {
            e.Graphics.FillPath(brush, path);
        }

        using var font = Theme.Icons(GlyphSize);
        TextRenderer.DrawText(e.Graphics, Glyph, font, ClientRectangle, Colour, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
