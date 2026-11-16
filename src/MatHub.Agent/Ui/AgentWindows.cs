using System.Drawing.Drawing2D;

namespace MatHub.Agent.Ui;

/// <summary>
/// The chrome every window of the tray and the setup shares (MatDesign AGENT-05, SETUP-01, TRAY-05): a white header of 84 px with the mark, the name and the
/// mode; the body; a grey footer of 58 px with <c>Version x · channel</c> at the left (SETUP-07) and the buttons at the right, the primary action first.
/// </summary>
internal class AgentWindow : Form
{
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 12, 0, 0), BackColor = Theme.Background };

    protected AgentWindow(string mode, Size client)
    {
        Text = $"{AgentInfo.DisplayName} – {mode}";
        Icon = UiApp.Icon;
        Font = Theme.Ui(9.5f);
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = client;
        KeyPreview = true;

        var head = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Theme.Surface };
        var logo = new PictureBox { Image = new Icon(UiApp.Icon, 48, 48).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Location = new Point(24, 20) };
        var name = new Label { Text = AgentInfo.DisplayName, AutoSize = true, Font = Theme.Display(14f), ForeColor = Theme.Text, Location = new Point(80, 17) };
        var sub = new Label { Text = mode, AutoSize = true, Font = Theme.Ui(9.5f), ForeColor = Theme.Muted, Location = new Point(82, 48) };
        head.Controls.AddRange([logo, name, sub]);

        var foot = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Background, Padding = new Padding(24, 0, 24, 0) };
        var version = new Label { Text = AgentInfo.VersionText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Font = Theme.Ui(9f) };
        foot.Controls.Add(version);
        foot.Controls.Add(_buttons);

        Body.Dock = DockStyle.Fill;
        Body.BackColor = Theme.Surface;
        Body.Padding = new Padding(24, 20, 24, 16);
        Controls.Add(Body);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border });
        Controls.Add(head);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border });
        Controls.Add(foot);
    }

    /// <summary>What the window shows between the header and the footer.</summary>
    protected Panel Body { get; } = new();

    /// <summary>Puts the buttons into the footer, in reading order (the primary action first, Cancel or Close last).</summary>
    protected void SetButtons(params FlatButton[] buttons)
    {
        _buttons.SuspendLayout();
        _buttons.Controls.Clear();
        foreach (var b in buttons)
        {
            b.Margin = new Padding(8, 0, 0, 0);
            b.MinimumSize = new Size(96, 34);
            _buttons.Controls.Add(b);
        }

        _buttons.ResumeLayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.Windows11(this);
    }

    /// <summary>A label of text that wraps at the width of the body.</summary>
    protected Label Paragraph(string text, Color colour, float size = 9.5f, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ClientSize.Width - Body.Padding.Horizontal, 0),
        ForeColor = colour,
        Font = Theme.Ui(size, style),
        Margin = new Padding(0, 0, 0, 8),
    };
}

/// <summary>The dot of a state (TRAY-02), never alone: a word goes with it.</summary>
internal sealed class StateDot : Control
{
    public StateDot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(12, 12);
        BackColor = Color.Transparent;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public TrayState State { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Theme.Dot(State));
        e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>
/// Info (TRAY-05), the window every tray opens first: the state as a dot, a word and a sentence; the facts as rows (computer, server, last contact, running since,
/// version, data folder); Open kiosk and Close. Never a secret (AGENT-03): the snapshot of the status channel has none.
/// </summary>
internal sealed class InfoWindow : AgentWindow
{
    private readonly StateDot _dot = new() { Location = new Point(0, 6) };
    private readonly Label _word = new() { AutoSize = true, Font = Theme.Ui(11f, FontStyle.Bold), ForeColor = Theme.Text, Location = new Point(20, 0) };
    private readonly Label _sentence = new() { AutoSize = true, Font = Theme.Ui(9.5f), ForeColor = Theme.Muted, Location = new Point(20, 26) };
    private readonly TableLayoutPanel _facts = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Surface, Padding = new Padding(0, 8, 0, 0) };

    public InfoWindow(AgentStatus status, Action openKiosk)
        : base(UiText.T("Info"), new Size(630, 440))
    {
        var state = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Surface };
        state.Controls.AddRange([_dot, _word, _sentence]);
        _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Body.Controls.Add(_facts);
        Body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border });
        Body.Controls.Add(state);

        var open = new FlatButton(UiText.T("OpenKiosk"), FlatButton.Kinds.Primary);
        var close = new FlatButton(UiText.T("Close"));
        open.Click += (_, _) => openKiosk();
        close.Click += (_, _) => Close();
        SetButtons(open, close);
        AcceptButton = open;
        CancelButton = close;
        SetStatus(status);
    }

    public void SetStatus(AgentStatus status)
    {
        _dot.State = status.State;
        _dot.Invalidate();
        _word.Text = TrayMenuModel.StateWord(status.State);
        _sentence.Text = TrayMenuModel.Sentence(status);

        _facts.SuspendLayout();
        _facts.Controls.Clear();
        _facts.RowStyles.Clear();
        var rows = new List<(string Label, string Value, bool Mono)>
        {
            (UiText.T("FactComputer"), UiText.Computer, false),
            (UiText.T("FactServer"), status.Server ?? "–", false),
            (UiText.T("FactLastContact"), status.LastContact is { } last ? UiText.When(last) : UiText.T("Never"), false),
        };
        if (status.Started is { } started && status.ServiceRuns)
        {
            rows.Add((UiText.T("FactStarted"), UiText.When(started), false));
        }

        rows.Add((UiText.T("FactVersion"), status.Version is { } v ? $"{v} · {AgentInfo.ChannelOf(v)}" : AgentInfo.VersionValue, false));
        rows.Add((UiText.T("FactData"), AgentInfo.DataDirectory, true));
        foreach (var (label, value, mono) in rows)
        {
            _facts.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui(9.5f), Margin = new Padding(0, 6, 12, 6) });
            _facts.Controls.Add(new Label { Text = value, AutoSize = true, ForeColor = Theme.Text, Font = mono ? Theme.Mono(9f) : Theme.Ui(9.5f), Margin = new Padding(0, 6, 0, 6), AutoEllipsis = true });
        }

        _facts.ResumeLayout();
    }
}

/// <summary>
/// The uninstall (SETUP-04), the window Installed apps opens: what goes, "Also delete my data" (off: the settings stay, so installing again finds them), a danger
/// button and Cancel. It needs administrator rights: <see cref="UiApp.Uninstall"/> asks for them before it opens.
/// </summary>
internal sealed class UninstallWindow : AgentWindow
{
    private readonly CheckBox _deleteData = new() { Text = UiText.T("UninstallData"), AutoSize = true, Checked = false, Font = Theme.Ui(9.5f), Margin = new Padding(0, 8, 0, 4) };
    private readonly Label _state;
    private readonly FlatButton _remove = new(UiText.T("UninstallButton"), FlatButton.Kinds.Danger);
    private readonly FlatButton _cancel = new(UiText.T("Cancel"));
    private readonly FlatButton _close = new(UiText.T("Close"), FlatButton.Kinds.Primary);
    private bool _busy;

    public UninstallWindow(Func<bool, int> uninstall)
        : base(UiText.T("Uninstall"), new Size(600, 560))
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface };
        _state = Paragraph(string.Empty, Theme.Text, 9.5f, FontStyle.Bold);
        flow.Controls.Add(Paragraph(UiText.T("UninstallTitle"), Theme.Text, 12f, FontStyle.Bold));
        flow.Controls.Add(Paragraph(UiText.T("UninstallWhat", AgentInfo.InstallDirectory), Theme.Text));
        flow.Controls.Add(_deleteData);
        flow.Controls.Add(Paragraph(UiText.T("UninstallKeeps", AgentInfo.DataDirectory), Theme.Muted, 9f));
        flow.Controls.Add(_state);
        Body.Controls.Add(flow);

        _remove.Click += async (_, _) =>
        {
            _busy = true;
            _remove.Enabled = _cancel.Enabled = _deleteData.Enabled = false;
            _state.ForeColor = Theme.Text;
            _state.Text = UiText.T("Removing");
            var deleteData = _deleteData.Checked;
            ExitCode = await Task.Run(() => uninstall(deleteData));
            _busy = false;
            Removed = ExitCode == 0;
            _state.ForeColor = Removed ? Theme.Ok : Theme.Danger;
            _state.Text = Removed
                ? UiText.T("Removed") + " " + (deleteData ? UiText.T("RemovedData") : UiText.T("RemovedKept", AgentInfo.DataDirectory))
                : UiText.T("RemoveFailed", ExitCode);
            SetButtons(_close);
            AcceptButton = CancelButton = _close;
            _close.Focus();
        };
        _cancel.Click += (_, _) => Close();
        _close.Click += (_, _) => Close();
        SetButtons(_remove, _cancel);
        CancelButton = _cancel;
    }

    /// <summary>0 removed, 1602 cancelled (as an MSI says it), else the code of the uninstall.</summary>
    public int ExitCode { get; private set; } = 1602;

    public bool Removed { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _cancel.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // the work cannot be cancelled or left (SETUP-02)
        e.Cancel = _busy;
        base.OnFormClosing(e);
    }
}
