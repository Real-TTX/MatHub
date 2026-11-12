using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace MatHub.Agent.Ui;

/// <summary>The look of the windows of the agent: the colours of MatHub (amber), the fonts of Windows 11 (Segoe UI Variable, Segoe Fluent Icons) and their fallbacks.</summary>
internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(0xB4, 0x53, 0x09);
    public static readonly Color AccentHover = Color.FromArgb(0x92, 0x40, 0x0E);
    public static readonly Color AccentSoft = Color.FromArgb(0xFD, 0xF0, 0xE1);
    public static readonly Color Background = Color.FromArgb(0xF3, 0xF3, 0xF3);
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE3, 0xE3, 0xE3);
    public static readonly Color Text = Color.FromArgb(0x1B, 0x1B, 0x1B);
    public static readonly Color Muted = Color.FromArgb(0x5C, 0x5C, 0x5C);
    public static readonly Color Ok = Color.FromArgb(0x0F, 0x7B, 0x0F);
    public static readonly Color Danger = Color.FromArgb(0xC4, 0x2B, 0x1C);
    public static readonly Color Warning = Color.FromArgb(0x9D, 0x5D, 0x00);

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

/// <summary>A flat button in the colours of MatHub: primary (amber), secondary (white with a border) or quiet (no border).</summary>
internal sealed class FlatButton : Button
{
    public enum Kinds
    {
        Primary,
        Secondary,
        Quiet,
    }

    public FlatButton(string text, Kinds kind = Kinds.Secondary, string? glyph = null)
    {
        Kind = kind;
        Glyph = glyph;
        Text = text;
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
        Font = Theme.Ui(9.5f, kind == Kinds.Primary ? FontStyle.Bold : FontStyle.Regular);
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
        FlatAppearance.BorderSize = Kind == Kinds.Secondary ? 1 : 0;
        FlatAppearance.BorderColor = Theme.Border;
        BackColor = Kind == Kinds.Primary ? Theme.Accent : Kind == Kinds.Secondary ? Theme.Surface : Color.Transparent;
        ForeColor = Kind == Kinds.Primary ? Color.White : Theme.Text;
        FlatAppearance.MouseOverBackColor = Kind == Kinds.Primary ? Theme.AccentHover : Color.FromArgb(0xF0, 0xF0, 0xF0);
        FlatAppearance.MouseDownBackColor = Kind == Kinds.Primary ? Theme.AccentHover : Color.FromArgb(0xE6, 0xE6, 0xE6);
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
