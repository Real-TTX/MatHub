using MatHub.Protocol;

namespace MatHub.Agent.Ui;

/// <summary>
/// The kiosk: the offers MatHub has for this computer, as tiles by category, with a search. It asks the service (and the service the server) when it opens
/// and every half minute, so it is always the current list; a start shows the state of the run on its tile until it is done.
/// </summary>
internal sealed class KioskWindow : Form
{
    private readonly TextBox _search = new() { PlaceholderText = UiText.T("Search"), Font = Theme.Ui(10f), BorderStyle = BorderStyle.None, BackColor = Theme.Surface };
    private readonly FlowLayoutPanel _categories = new() { Dock = DockStyle.Top, Height = 48, Padding = new Padding(20, 10, 20, 0), BackColor = Theme.Background, WrapContents = false, AutoScroll = false };
    private readonly FlowLayoutPanel _tiles = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20, 8, 8, 20), BackColor = Theme.Background };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Font = Theme.Ui(8.5f) };
    private readonly Label _message = new() { AutoSize = false, TextAlign = ContentAlignment.TopCenter, ForeColor = Theme.Muted, Font = Theme.Ui(11f) };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _follow = new() { Interval = 2_000 };
    private readonly Dictionary<long, RunState> _runs = [];
    private readonly Dictionary<long, Tile> _byItem = [];
    private KioskOffer _offer = new();
    private string _category = string.Empty;
    private Task? _load;

    public KioskWindow()
    {
        Text = UiText.T("KioskTitle");
        Icon = UiApp.Icon;
        Font = Theme.Ui(9.5f);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(1000, 680);
        MinimumSize = new Size(560, 440);

        var head = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Theme.Surface, Padding = new Padding(24, 16, 24, 16) };
        var logo = new PictureBox { Image = UiApp.Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Location = new Point(24, 20) };
        var title = new Label { Text = UiText.T("KioskTitle"), AutoSize = true, Font = Theme.Display(16f), Location = new Point(80, 16), ForeColor = Theme.Text };
        var sub = new Label { Text = UiText.T("KioskSub", UiText.Computer), AutoSize = true, ForeColor = Theme.Muted, Location = new Point(82, 48), Font = Theme.Ui(9.5f) };
        var refresh = new FlatButton(UiText.T("Refresh"), FlatButton.Kinds.Secondary, Theme.Glyph("refresh")) { Margin = new Padding(12, 0, 0, 0) };

        // the search: a field with a glyph and a round border, as high as the button beside it
        var searchBox = new Card { Size = new Size(260, 34), Padding = new Padding(34, 8, 10, 6), Radius = 4, Margin = Padding.Empty };
        var lens = new Label { Text = Theme.Glyph("search"), Font = Theme.Icons(10f), ForeColor = Theme.Muted, AutoSize = false, Size = new Size(26, 32), Location = new Point(8, 1), TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.Surface };
        _search.Dock = DockStyle.Fill;
        searchBox.Controls.AddRange([_search, lens]);
        lens.Click += (_, _) => _search.Focus();
        var tools = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 9, 0, 0), BackColor = Theme.Surface };
        tools.Controls.AddRange([searchBox, refresh]);
        head.Controls.AddRange([logo, title, sub, tools]);
        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border };

        var foot = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Theme.Background, Padding = new Padding(24, 0, 24, 0) };
        foot.Controls.Add(_status);

        _message.Size = new Size(600, 120);
        Controls.Add(_tiles);
        Controls.Add(_categories);
        Controls.Add(line);
        Controls.Add(head);
        Controls.Add(foot);

        refresh.Click += async (_, _) => await LoadAsync();
        _search.TextChanged += (_, _) => Render();
        _tiles.Resize += (_, _) => Fit();
        _refresh.Tick += async (_, _) => await LoadAsync(quiet: true);
        _follow.Tick += async (_, _) => await FollowAsync();
        Shown += async (_, _) =>
        {
            Theme.Windows11(this);
            _search.Focus();
            await LoadAsync();
        };
        _refresh.Start();
        _follow.Start();
        KeyPreview = true;
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F5)
            {
                await LoadAsync();
            }
            else if (e.KeyCode == Keys.Escape && _search.Text.Length > 0)
            {
                _search.Text = string.Empty;
            }
        };
    }

    /// <summary>Asks the service for the offers (and the service the server): always the current ones.</summary>
    public Task LoadAsync(bool quiet = false)
    {
        // a load that runs is the answer to a second one
        if (_load is { IsCompleted: false })
        {
            return _load;
        }

        return _load = LoadNowAsync(quiet);
    }

    private async Task LoadNowAsync(bool quiet)
    {
        if (!quiet && _offer.Items.Count == 0)
        {
            Show(UiText.T("Loading"), "clock");
        }

        try
        {
            var response = await UiPipe.RequestAsync(new PipeRequest { Op = "kiosk" }, TimeSpan.FromSeconds(30));
            if (response.Error is not null || response.Offer is null)
            {
                if (_offer.Items.Count == 0)
                {
                    Show(UiText.T("Offline"), "offline");
                }

                _status.Text = UiText.T("Offline");
                return;
            }

            _offer = response.Offer;
            foreach (var item in _offer.Items.Where(i => i.Last is { } last && !_runs.ContainsKey(i.Id)))
            {
                _runs[item.Id] = item.Last!;
            }

            _status.Text = UiText.T("Updated", DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
            Render();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            Show(UiText.T("NoAgent"), "offline");
            _status.Text = UiText.T("NoAgent");
        }
    }

    private void Show(string text, string glyph)
    {
        _tiles.SuspendLayout();
        _tiles.Controls.Clear();
        var box = new Panel { Size = new Size(Math.Max(320, _tiles.ClientSize.Width - 60), 200), BackColor = Theme.Background, Margin = new Padding(0, 40, 0, 0) };
        var icon = new GlyphBox(Theme.Glyph(glyph), Theme.Muted, Color.FromArgb(0xE9, 0xE9, 0xE9), 56, 22f) { Location = new Point((box.Width - 56) / 2, 10) };
        _message.Text = text;
        _message.Location = new Point(0, 82);
        _message.Width = box.Width;
        box.Controls.AddRange([icon, _message]);
        _tiles.Controls.Add(box);
        _tiles.ResumeLayout();
    }

    private void Render()
    {
        // the categories as tabs
        var categories = _offer.Items.Select(i => i.Category ?? string.Empty).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (_category.Length > 0 && !categories.Contains(_category, StringComparer.OrdinalIgnoreCase))
        {
            _category = string.Empty;
        }

        _categories.SuspendLayout();
        _categories.Controls.Clear();
        _categories.Visible = categories.Count > 0;
        foreach (var category in new[] { string.Empty }.Concat(categories))
        {
            var count = category.Length == 0 ? _offer.Items.Count : _offer.Items.Count(i => string.Equals(i.Category, category, StringComparison.OrdinalIgnoreCase));
            var tab = new FlatButton($"{(category.Length == 0 ? UiText.T("All") : category)}  {count}", category == _category ? FlatButton.Kinds.Primary : FlatButton.Kinds.Quiet)
            {
                Height = 30,
                Margin = new Padding(0, 0, 6, 0),
                Font = Theme.Ui(9.5f, category == _category ? FontStyle.Bold : FontStyle.Regular),
            };
            var chosen = category;
            tab.Click += (_, _) =>
            {
                _category = chosen;
                Render();
            };
            _categories.Controls.Add(tab);
        }

        _categories.ResumeLayout();

        var q = _search.Text.Trim();
        var items = _offer.Items
            .Where(i => _category.Length == 0 || string.Equals(i.Category, _category, StringComparison.OrdinalIgnoreCase))
            .Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (i.Description ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (_offer.Items.Count == 0)
        {
            Show(UiText.T("Empty"), "box");
            return;
        }

        if (items.Count == 0)
        {
            Show(UiText.T("NoMatch"), "search");
            return;
        }

        _tiles.SuspendLayout();
        _tiles.Controls.Clear();
        _byItem.Clear();
        foreach (var item in items)
        {
            var tile = new Tile(item, StartAsync);
            tile.SetState(_runs.GetValueOrDefault(item.Id));
            _byItem[item.Id] = tile;
            _tiles.Controls.Add(tile);
        }

        Fit();
        _tiles.ResumeLayout();
    }

    /// <summary>One column on a narrow window, two from 860 px on, three from 1300 px.</summary>
    private void Fit()
    {
        var width = _tiles.ClientSize.Width - _tiles.Padding.Horizontal - 2;
        var scale = DeviceDpi / 96.0;
        var columns = width >= 1300 * scale ? 3 : width >= 820 * scale ? 2 : 1;
        var tileWidth = width / columns - 12;
        foreach (Control c in _tiles.Controls)
        {
            if (c is Tile)
            {
                c.Width = tileWidth;
            }
        }
    }

    private async Task StartAsync(KioskOfferItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Confirm))
        {
            using var confirm = new DialogWindow(new UiDialog { Kind = UiKinds.Question, Title = item.Name, Text = item.Confirm, TimeoutSeconds = 600 });
            confirm.ShowDialog(this);
            if (confirm.Reply.Answer != "yes")
            {
                return;
            }
        }

        _byItem.GetValueOrDefault(item.Id)?.SetState(new RunState { Status = RunStates.Queued });
        try
        {
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var response = await UiPipe.RequestAsync(new PipeRequest { Op = "start", Id = item.Id, User = user }, TimeSpan.FromSeconds(30));
            if (response.Error is not null)
            {
                _byItem.GetValueOrDefault(item.Id)?.SetState(_runs.GetValueOrDefault(item.Id));
                _status.Text = UiText.T("Offline");
                return;
            }

            if (response.RunId is not { } runId || runId == 0)
            {
                _status.Text = UiText.T("Gone");
                await LoadAsync(quiet: true);
                return;
            }

            _runs[item.Id] = new RunState { RunId = runId, Status = RunStates.Queued };
            _byItem.GetValueOrDefault(item.Id)?.SetState(_runs[item.Id]);
            _status.Text = UiText.T("Started", item.Name);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            _status.Text = UiText.T("NoAgent");
            _byItem.GetValueOrDefault(item.Id)?.SetState(_runs.GetValueOrDefault(item.Id));
        }
    }

    /// <summary>The runs that are not done yet: their state on their tiles, every two seconds.</summary>
    private async Task FollowAsync()
    {
        foreach (var (itemId, run) in _runs.Where(r => r.Value.Status is RunStates.Queued or RunStates.Running && r.Value.RunId > 0).ToList())
        {
            try
            {
                var response = await UiPipe.RequestAsync(new PipeRequest { Op = "state", RunId = run.RunId }, TimeSpan.FromSeconds(15));
                if (response.State is not { } state)
                {
                    continue;
                }

                _runs[itemId] = state;
                _byItem.GetValueOrDefault(itemId)?.SetState(state);
                var name = _offer.Items.FirstOrDefault(i => i.Id == itemId)?.Name ?? string.Empty;
                if (state.Status == RunStates.Succeeded)
                {
                    _status.Text = UiText.T("Finished", name);
                }
                else if (state.Status == RunStates.Failed)
                {
                    _status.Text = UiText.T("FailedNotice", name);
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refresh.Dispose();
        _follow.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>An offer: its icon, its name and text, the state of its last run and the button that starts it.</summary>
    private sealed class Tile : Card
    {
        private readonly KioskOfferItem _item;
        private readonly FlatButton _start;
        private readonly Label _state = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Ui(8.5f), ForeColor = Theme.Muted, AutoEllipsis = true };

        public Tile(KioskOfferItem item, Func<KioskOfferItem, Task> start)
        {
            _item = item;
            Height = 112;
            Margin = new Padding(0, 0, 12, 12);
            var icon = new GlyphBox(Theme.Glyph(item.Icon), Theme.Accent, Theme.AccentSoft, 48, 19f) { Location = new Point(16, 18) };
            var name = new Label { Text = item.Name, AutoSize = false, Location = new Point(78, 14), Height = 24, Font = Theme.Ui(11f, FontStyle.Bold), ForeColor = Theme.Text, AutoEllipsis = true, BackColor = Theme.Surface };
            var text = new Label { Text = item.Description ?? string.Empty, AutoSize = false, Location = new Point(78, 38), Height = 38, Font = Theme.Ui(9f), ForeColor = Theme.Muted, AutoEllipsis = true, BackColor = Theme.Surface };
            _state.Location = new Point(78, 80);
            _state.Height = 20;
            _state.BackColor = Theme.Surface;
            _start = new FlatButton(UiText.T("Start"), FlatButton.Kinds.Primary, Theme.Glyph("play")) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _start.MinimumSize = new Size(116, 34);
            _start.Click += async (_, _) => await start(_item);
            Controls.AddRange([icon, name, text, _state, _start]);
            Resize += (_, _) =>
            {
                _start.Location = new Point(Width - 16 - _start.PreferredSize.Width, (Height - _start.Height) / 2 - 8);
                name.Width = text.Width = Math.Max(60, _start.Left - 12 - 78);
                _state.Width = Width - 78 - 16;
            };
        }

        public void SetState(RunState? state)
        {
            var busy = state?.Status is RunStates.Queued or RunStates.Running;
            _start.Enabled = !busy;
            _start.Text = busy ? UiText.T(state!.Status == RunStates.Queued ? "Queued" : "Running") : state is null ? UiText.T("Start") : UiText.T("Again");
            _start.Left = Width - 16 - _start.PreferredSize.Width;
            switch (state?.Status)
            {
                case null:
                    _state.Text = string.Empty;
                    break;
                case RunStates.Queued or RunStates.Running:
                    _state.ForeColor = Theme.Accent;
                    _state.Text = "●  " + (state.Steps > 1 ? UiText.T("Step", state.Step, state.Steps) : UiText.T(state.Status == RunStates.Queued ? "Queued" : "Running"));
                    break;
                case RunStates.Succeeded:
                    _state.ForeColor = Theme.Ok;
                    _state.Text = "✓  " + UiText.T("Done") + (state.Finished is { } done ? " · " + UiText.When(done) : string.Empty);
                    break;
                case RunStates.Failed:
                    _state.ForeColor = Theme.Danger;
                    _state.Text = "✕  " + UiText.T("Failed") + (state.Finished is { } failed ? " · " + UiText.When(failed) : string.Empty);
                    break;
                default:
                    _state.ForeColor = Theme.Muted;
                    _state.Text = UiText.T("Cancelled");
                    break;
            }
        }
    }
}
