using System.Globalization;
using MatHub.Protocol;

namespace MatHub.Agent.Ui;

/// <summary>
/// A dialog of a script for the person at the computer: a message (OK), a question (yes, no) or a form, on one or more pages (a wizard with Back and Next). The
/// values of the fields come back by their names; a required field has to be filled before it goes on. After its time the dialog closes with "timeout".
/// </summary>
internal sealed class DialogWindow : Form
{
    private readonly UiDialog _dialog;
    private readonly List<UiPage> _pages;
    private readonly Dictionary<string, Control> _inputs = new(StringComparer.Ordinal);
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, AutoScroll = true, Padding = new Padding(28, 18, 28, 12) };
    private readonly Label _step = new() { AutoSize = true, ForeColor = Theme.Accent, Font = Theme.Ui(8.5f, FontStyle.Bold) };
    private readonly Label _pageTitle = new() { AutoSize = true, ForeColor = Theme.Text };
    private readonly Label _error = new() { AutoSize = false, ForeColor = Theme.Danger, Font = Theme.Ui(9f), TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly FlatButton _back = new(UiText.T("Back"));
    private readonly FlatButton _next;
    private readonly FlatButton _cancel;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private int _page;
    private int _left;

    public DialogWindow(UiDialog dialog)
    {
        _dialog = dialog;
        _pages = dialog.Kind == UiKinds.Form && dialog.Pages.Count > 0 ? [.. dialog.Pages] : [new UiPage()];
        Reply = new UiReply { Answer = dialog.Kind == UiKinds.Question ? "no" : "cancel" };
        _left = Math.Clamp(dialog.TimeoutSeconds, 10, 86400);

        Text = string.IsNullOrWhiteSpace(dialog.Title) ? "MatHub" : dialog.Title;
        Icon = UiApp.Icon;
        Font = Theme.Ui(9.5f);
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(560, dialog.Kind == UiKinds.Form ? 470 : 260);

        // the head: the icon of the kind, the title, the text of the dialog
        var head = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Background, Padding = new Padding(24, 18, 24, 12) };
        var (glyph, colour, soft) = dialog.Kind switch
        {
            UiKinds.Question => ("question", Theme.Accent, Theme.AccentSoft),
            UiKinds.Form => ("settings", Theme.Accent, Theme.AccentSoft),
            _ => dialog.Level switch
            {
                "warning" => ("warning", Theme.Warning, Color.FromArgb(0xFF, 0xF4, 0xCE)),
                "error" => ("error", Theme.Danger, Color.FromArgb(0xFD, 0xE7, 0xE9)),
                _ => ("info", Theme.Accent, Theme.AccentSoft),
            },
        };
        var icon = new GlyphBox(Theme.Glyph(glyph), colour, soft, 48, 19f) { Location = new Point(24, 20) };
        var title = new Label { Text = Text, AutoSize = false, Location = new Point(88, 18), Size = new Size(440, 30), Font = Theme.Display(14f), ForeColor = Theme.Text, AutoEllipsis = true };
        var lead = new Label
        {
            Text = dialog.Kind == UiKinds.Form ? dialog.Text ?? string.Empty : string.Empty, AutoSize = false, Location = new Point(88, 50), Size = new Size(440, 36),
            ForeColor = Theme.Muted, Font = Theme.Ui(9.5f), AutoEllipsis = true,
        };
        if (lead.Text.Length == 0)
        {
            // a message, a question: the title in the middle of the head
            title.Top = 30;
        }

        head.Controls.AddRange([icon, title, lead]);
        var line = new Panel { Dock = DockStyle.Top, Height = 2, BackColor = Theme.Accent };

        // the foot: the error, the buttons
        var foot = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Background, Padding = new Padding(24, 14, 24, 14), ColumnCount = 4 };
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var question = dialog.Kind == UiKinds.Question;
        _next = new FlatButton(question ? UiText.T("Yes") : dialog.Kind == UiKinds.Form && _pages.Count > 1 ? UiText.T("Next") : UiText.T("Ok"), FlatButton.Kinds.Primary);
        _cancel = new FlatButton(question ? UiText.T("No") : UiText.T("Cancel"));
        foreach (var b in new[] { _back, _cancel, _next })
        {
            b.Margin = new Padding(8, 0, 0, 0);
            b.MinimumSize = new Size(96, 34);
        }

        foot.Controls.Add(_error, 0, 0);
        foot.Controls.Add(_back, 1, 0);
        foot.Controls.Add(_cancel, 2, 0);
        foot.Controls.Add(_next, 3, 0);
        _back.Visible = false;
        _cancel.Visible = dialog.Kind != UiKinds.Message;
        AcceptButton = _next;
        CancelButton = dialog.Kind == UiKinds.Message ? _next : _cancel;

        _next.Click += (_, _) => Next();
        _back.Click += (_, _) => Show(_page - 1);
        _cancel.Click += (_, _) => Finish(question ? "no" : "cancel");
        _clock.Tick += (_, _) => Tick();

        Controls.Add(_body);
        Controls.Add(line);
        Controls.Add(head);
        Controls.Add(foot);
        BuildInputs();
        Show(0);
        _clock.Start();
    }

    /// <summary>What the person answered (and the values of the fields).</summary>
    public UiReply Reply { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.Windows11(this);
        Activate();
        _inputs.Values.FirstOrDefault(c => c.Visible && c.CanFocus)?.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _clock.Stop();
        base.OnFormClosing(e);
    }

    /// <summary>The answer the dialog gives by itself (a test, a snapshot): OK with the values set in advance.</summary>
    public UiReply AutoAnswer()
    {
        Collect("ok");
        if (_dialog.Kind == UiKinds.Question)
        {
            Reply = Reply with { Answer = "yes" };
        }

        return Reply;
    }

    /// <summary>Goes to a page (a snapshot of every page of a wizard).</summary>
    public int Pages => _pages.Count;

    public void ShowPage(int page) => Show(page);

    private void BuildInputs()
    {
        foreach (var field in _pages.SelectMany(p => p.Fields).Where(f => f.Kind != "info" && f.Name.Length > 0))
        {
            Control input = field.Kind switch
            {
                "check" => new CheckBox { Text = field.Label, AutoSize = true, Checked = field.Default is "true" or "1" or "yes" or "ja", Font = Theme.Ui(9.5f) },
                "choice" => Choice(field),
                "number" => new NumericUpDown { Minimum = -1_000_000, Maximum = 1_000_000_000, Value = decimal.TryParse(field.Default, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, -1_000_000, 1_000_000_000) : 0, Width = 160 },
                "multiline" => new TextBox { Multiline = true, Height = 76, ScrollBars = ScrollBars.Vertical, Text = field.Default ?? string.Empty },
                "password" => new TextBox { UseSystemPasswordChar = true, Text = string.Empty },
                _ => new TextBox { Text = field.Default ?? string.Empty },
            };
            input.Tag = field;
            _inputs[field.Name] = input;
        }
    }

    private static ComboBox Choice(UiField field)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        box.Items.AddRange([.. (field.Options ?? []).Cast<object>()]);
        var index = field.Default is null ? -1 : box.Items.IndexOf(field.Default);
        box.SelectedIndex = index >= 0 ? index : field.Required || box.Items.Count == 0 ? -1 : 0;
        return box;
    }

    private void Show(int page)
    {
        _page = Math.Clamp(page, 0, _pages.Count - 1);
        _error.Text = string.Empty;
        _error.ForeColor = Theme.Muted;
        var current = _pages[_page];
        _body.SuspendLayout();
        _body.Controls.Clear();
        var flow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Theme.Surface, Padding = Padding.Empty };
        flow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var width = ClientSize.Width - _body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;

        if (_dialog.Kind == UiKinds.Form)
        {
            if (_pages.Count > 1)
            {
                _step.Text = UiText.T("PageOf", _page + 1, _pages.Count).ToUpperInvariant();
                flow.Controls.Add(_step);
            }

            if (!string.IsNullOrWhiteSpace(current.Title))
            {
                _pageTitle.Text = current.Title;
                _pageTitle.Font = Theme.Ui(12f, FontStyle.Bold);
                _pageTitle.Margin = new Padding(0, 2, 0, 4);
                flow.Controls.Add(_pageTitle);
            }

            if (!string.IsNullOrWhiteSpace(current.Text))
            {
                flow.Controls.Add(Paragraph(current.Text, width));
            }

            foreach (var field in current.Fields)
            {
                if (field.Kind == "info")
                {
                    flow.Controls.Add(Paragraph(field.Label, width, Theme.Muted));
                    continue;
                }

                if (!_inputs.TryGetValue(field.Name, out var input))
                {
                    continue;
                }

                if (field.Kind != "check")
                {
                    flow.Controls.Add(new Label
                    {
                        Text = field.Label + (field.Required ? " *" : string.Empty), AutoSize = true, Font = Theme.Ui(9.5f, FontStyle.Bold), ForeColor = Theme.Text,
                        Margin = new Padding(0, 10, 0, 4),
                    });
                }

                input.Margin = new Padding(0, field.Kind == "check" ? 10 : 0, 0, 2);
                if (input is TextBox or ComboBox)
                {
                    input.Width = Math.Min(width, 480);
                }

                flow.Controls.Add(input);
            }
        }
        else
        {
            flow.Controls.Add(Paragraph(_dialog.Text ?? string.Empty, width, Theme.Text, 10.5f));
        }

        _body.Controls.Add(flow);
        _body.ResumeLayout();
        _back.Visible = _dialog.Kind == UiKinds.Form && _page > 0;
        if (_dialog.Kind == UiKinds.Form)
        {
            _next.Text = _page == _pages.Count - 1 ? UiText.T("Finish") : UiText.T("Next");
        }

        _inputs.Values.FirstOrDefault(c => flow.Controls.Contains(c) && c.CanFocus)?.Focus();
    }

    private static Label Paragraph(string text, int width, Color? colour = null, float size = 9.5f) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = colour ?? Theme.Muted, Font = Theme.Ui(size), Margin = new Padding(0, 4, 0, 8),
    };

    private void Next()
    {
        if (_dialog.Kind == UiKinds.Question)
        {
            Finish("yes");
            return;
        }

        if (_dialog.Kind != UiKinds.Form)
        {
            Finish("ok");
            return;
        }

        // the required fields of this page first
        var missing = _pages[_page].Fields.Where(f => f.Required && f.Kind != "check" && _inputs.TryGetValue(f.Name, out var c) && string.IsNullOrWhiteSpace(Value(c))).ToList();
        if (missing.Count > 0)
        {
            _error.ForeColor = Theme.Danger;
            _error.Text = UiText.T("Required", string.Join(", ", missing.Select(f => f.Label)));
            _inputs[missing[0].Name].Focus();
            return;
        }

        if (_page < _pages.Count - 1)
        {
            Show(_page + 1);
            return;
        }

        Finish("ok");
    }

    private void Tick()
    {
        _left--;
        if (_left <= 30 && _error.ForeColor != Theme.Danger)
        {
            _error.ForeColor = Theme.Muted;
            _error.Text = UiText.T("Closes", _left);
        }

        if (_left <= 0)
        {
            Finish("timeout");
        }
    }

    private void Finish(string answer)
    {
        Collect(answer);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Collect(string answer)
    {
        var values = answer == "ok" ? _inputs.ToDictionary(i => i.Key, i => Value(i.Value), StringComparer.Ordinal) : null;
        Reply = new UiReply { Answer = answer, Values = values };
    }

    private static string Value(Control control) => control switch
    {
        CheckBox c => c.Checked ? "true" : "false",
        ComboBox c => c.SelectedItem as string ?? string.Empty,
        NumericUpDown n => n.Value.ToString(CultureInfo.InvariantCulture),
        _ => control.Text,
    };
}

/// <summary>"Please wait": a small window in the corner while the steps of a script run; it closes with the next dialog or the end of the job.</summary>
internal sealed class ProgressWindow : Form
{
    private readonly Label _title = new() { AutoSize = false, Location = new Point(70, 16), Size = new Size(290, 22), Font = Theme.Ui(10.5f, FontStyle.Bold), ForeColor = Theme.Text, AutoEllipsis = true };
    private readonly Label _text = new() { AutoSize = false, Location = new Point(70, 40), Size = new Size(290, 42), Font = Theme.Ui(9f), ForeColor = Theme.Muted, AutoEllipsis = true };

    public ProgressWindow()
    {
        Text = "MatHub";
        Icon = UiApp.Icon;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Surface;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(380, 118);
        var icon = new GlyphBox(Theme.Glyph("settings"), Theme.Accent, Theme.AccentSoft, 40, 16f) { Location = new Point(18, 18) };
        var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Location = new Point(70, 90), Size = new Size(290, 6) };
        Controls.AddRange([icon, _title, _text, bar]);
    }

    public void Set(UiDialog dialog)
    {
        _title.Text = string.IsNullOrWhiteSpace(dialog.Title) ? "MatHub" : dialog.Title;
        _text.Text = string.IsNullOrWhiteSpace(dialog.Text) ? UiText.T("Wait") : dialog.Text;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    protected override bool ShowWithoutActivation => true;
}
