using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MatHub.Protocol;

namespace MatHub.Agent.Ui;

/// <summary>
/// The windows of the agent in the session of the person signed in:
///   mathub-agent tray       the symbol beside the clock (started at every sign-in): the state, Info, the kiosk, the dialogs of scripts
///   mathub-agent kiosk      the kiosk window (the start menu entry "MatHub Kiosk")
///   mathub-agent uninstall  the window of the uninstall (Installed apps opens it)
/// For a test and the pictures of the documentation, <c>--snapshot &lt;file or folder&gt;</c> draws the windows into PNG files without showing them, and
/// <c>--auto</c> answers every dialog by itself (OK with the values set in advance).
/// </summary>
internal static class UiApp
{
    private static readonly Lazy<Icon> LazyIcon = new(() =>
    {
        using var stream = typeof(UiApp).Assembly.GetManifestResourceStream("MatHub.Agent.mathub-agent.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    });

    public static Icon Icon => LazyIcon.Value;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int Run(string mode, string[] args)
    {
        var snapshot = Option(args, "--snapshot");
        var auto = args.Contains("--auto") || snapshot is not null;
        return OnUiThread(() => mode == "tray" ? Tray(auto, snapshot, Option(args, "--after")) : Kiosk(snapshot));
    }

    /// <summary>A WinForms thread (STA, the DPI of each screen).</summary>
    private static int OnUiThread(Func<int> run)
    {
        var exit = 0;
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            exit = run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    /// <summary>
    /// <c>mathub-agent uninstall</c> with a window (SETUP-04): asks for administrator rights first (SETUP-03: once, when it starts), then the window in the mode
    /// Uninstall. <c>--snapshot file.png</c> draws the window and removes nothing. Exit code: 0 removed, 1602 cancelled, 1223 the rights were refused.
    /// </summary>
    public static int Uninstall(string[] args)
    {
        var snapshot = Option(args, "--snapshot");
        if (snapshot is null && !Setup.IsAdministrator())
        {
            try
            {
                var elevated = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                foreach (var a in args)
                {
                    elevated.ArgumentList.Add(a);
                }

                using var process = Process.Start(elevated)!;
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // the person said no to the rights
                return 1223;
            }
        }

        var removed = false;
        var exit = OnUiThread(() =>
        {
            using var window = new UninstallWindow(deleteData => Setup.Uninstall(deleteData));
            if (snapshot is not null)
            {
                SnapWindow(window, snapshot);
                return 0;
            }

            Application.Run(window);
            removed = window.Removed;
            return window.ExitCode;
        });

        if (removed)
        {
            Setup.RemoveProgramFolderLater();
        }

        return exit;
    }

    private static int Tray(bool auto, string? snapshots, string? after)
    {
        // a new version of the tray waits until the old one has gone
        if (int.TryParse(after, out var pid))
        {
            try
            {
                Process.GetProcessById(pid).WaitForExit(15_000);
            }
            catch (ArgumentException)
            {
                // gone already
            }
        }

        using var single = new Mutex(true, @"Local\MatHubAgentTray-" + AgentInfo.PipeName, out var first);
        if (!first && snapshots is null)
        {
            return 0;
        }

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayApp(auto, snapshots));
        return 0;
    }

    private static int Kiosk(string? snapshot)
    {
        if (snapshot is null)
        {
            using var single = new Mutex(true, @"Local\MatHubKiosk-" + AgentInfo.PipeName, out var first);
            if (!first)
            {
                // the kiosk is open already: it comes to the front
                var window = FindWindow(null, UiText.T("KioskTitle"));
                if (window != IntPtr.Zero)
                {
                    ShowWindow(window, 9);
                    SetForegroundWindow(window);
                }

                return 0;
            }

            Application.Run(new KioskWindow());
            return 0;
        }

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var kiosk = new KioskWindow();
        var done = false;
        kiosk.Shown += async (_, _) =>
        {
            await kiosk.LoadAsync();
            await Task.Delay(400);
            Snap(kiosk, snapshot);
            done = true;
            kiosk.Close();
        };
        Offscreen(kiosk);
        kiosk.Show();
        while (!done && kiosk.Visible)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }

        return 0;
    }

    /// <summary>A window far off the screen: it is drawn, but nobody sees it.</summary>
    public static void Offscreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
    }

    /// <summary>Shows a window off the screen, draws it into a file and hides it again.</summary>
    public static void SnapWindow(Form form, string file)
    {
        Offscreen(form);
        form.Show();
        Snap(form, file);
        form.Hide();
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    /// <summary>The window as a PNG file, with the frame Windows draws (PrintWindow), else as the controls draw themselves.</summary>
    public static void Snap(Form form, string file)
    {
        form.Refresh();
        for (var i = 0; i < 10; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }

        using var bitmap = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            var dc = g.GetHdc();
            var printed = PrintWindow(form.Handle, dc, 2);
            g.ReleaseHdc(dc);
            if (!printed)
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        bitmap.Save(file, ImageFormat.Png);
    }
}

/// <summary>
/// The icon of the tray with the dot of its state (TRAY-02): a dot at the bottom right, with a cut-out of the background between the dot and the mark, so that it
/// reads on any task bar; drawn at runtime from the mark of the app (the geometry of MatDesktop's <c>MatTrayIcons</c>: centre at 75 %, radius 22 %, gap 8 %).
/// </summary>
internal static class TrayIcons
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    public static Bitmap Draw(TrayState state, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var mark = new Icon(UiApp.Icon, size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawIcon(mark, new Rectangle(0, 0, size, size));

        var centre = size * 0.75f;
        var radius = size * 0.22f;
        var gap = Math.Max(1f, size * 0.08f);
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
        {
            g.FillEllipse(clear, centre - radius - gap, centre - radius - gap, (radius + gap) * 2, (radius + gap) * 2);
        }

        g.CompositingMode = CompositingMode.SourceOver;
        using var dot = new SolidBrush(Theme.Dot(state));
        g.FillEllipse(dot, centre - radius, centre - radius, radius * 2, radius * 2);
        return bitmap;
    }

    /// <summary>The icon for the notification area; the caller destroys the one it replaces with <see cref="Release"/>.</summary>
    public static Icon For(TrayState state, int size)
    {
        using var bitmap = Draw(state, size);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public static void Release(Icon? icon)
    {
        if (icon is null)
        {
            return;
        }

        var handle = icon.Handle;
        icon.Dispose();
        DestroyIcon(handle);
    }
}

/// <summary>
/// The symbol beside the clock (TRAY-01 … TRAY-05, AGENT-04): its dot and its tooltip say the state of the agent, which it reads from the status channel of the
/// service; a left click opens the kiosk, the right click the menu (the name and the version, the state, Open MatHub Kiosk, Info …, the service with the shield,
/// Quit). It also shows what the scripts of the agent ask: it waits on the pipe of the service for a dialog, shows it and gives back the answer. Quit ends this
/// tray only, never the service; it comes back with the next sign-in. A newer agent (after an update of the service) starts its own tray and this one ends.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new() { Font = Theme.Ui(9.5f), ShowImageMargin = true };
    private readonly SynchronizationContext _ui;
    private readonly bool _auto;
    private readonly string? _snapshots;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 10_000 };
    private AgentStatus _status = new() { Condition = AgentCondition.Connecting, Version = AgentInfo.Version };
    private bool _serviceInstalled;
    private Icon? _icon;
    private ProgressWindow? _progress;
    private KioskWindow? _kiosk;
    private InfoWindow? _info;
    private int _shot;
    private volatile bool _stop;

    public TrayApp(bool auto, string? snapshots)
    {
        _auto = auto;
        _snapshots = snapshots;
        _ui = SynchronizationContext.Current!;
        _tray = new NotifyIcon { ContextMenuStrip = _menu, Visible = false };

        // TRAY-03: a left click is the default item, the right click opens the menu (the system does that)
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenKiosk();
            }
        };
        _tray.BalloonTipClicked += (_, _) => OpenKiosk();

        // the state before the window shows: what the service says now
        Apply(Task.Run(ReadStatusAsync).GetAwaiter().GetResult());
        _tray.Visible = snapshots is null;
        if (snapshots is not null)
        {
            SnapTray(snapshots);
        }

        _poll.Tick += async (_, _) => Apply(await Task.Run(ReadStatusAsync));
        _poll.Start();
        _ = Task.Run(LoopAsync);
    }

    /// <summary>The snapshot of the service; without one, whether the service is not installed or does not run.</summary>
    private async Task<(AgentStatus Status, bool Installed)> ReadStatusAsync()
    {
        var installed = ServiceInstalled();
        var status = await StatusPipe.ReadAsync(TimeSpan.FromSeconds(3));
        if (status is not null)
        {
            return (status, installed);
        }

        // a test with its own data folder has no service: the agent of the test does not run
        var condition = installed || AgentInfo.OwnDataDirectory ? AgentCondition.ServiceStopped : AgentCondition.NotInstalled;
        return (new AgentStatus { Condition = condition, Server = _status.Server, LastContact = _status.LastContact, Version = AgentInfo.Version }, installed);
    }

    private static bool ServiceInstalled()
    {
        if (AgentInfo.OwnDataDirectory)
        {
            return false;
        }

        try
        {
            using var service = new System.ServiceProcess.ServiceController(AgentInfo.ServiceName);
            _ = service.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The state on the icon, in the tooltip, in the menu and in Info.</summary>
    private void Apply((AgentStatus Status, bool Installed) read)
    {
        if (_stop)
        {
            return;
        }

        var changed = read.Status.State != _status.State || _icon is null;
        _status = read.Status;
        _serviceInstalled = read.Installed;
        if (changed)
        {
            var old = _icon;
            _icon = TrayIcons.For(_status.State, SystemInformation.SmallIconSize.Width);
            _tray.Icon = _icon;
            TrayIcons.Release(old);
        }

        var tip = TrayMenuModel.Tooltip(_status.State);
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        BuildMenu();
        _info?.SetStatus(_status);
    }

    private void BuildMenu()
    {
        _menu.SuspendLayout();
        _menu.Items.Clear();
        foreach (var entry in TrayMenuModel.Build(_status, _serviceInstalled))
        {
            if (entry.Role == TrayRole.Separator)
            {
                _menu.Items.Add(new ToolStripSeparator());
                continue;
            }

            var item = new ToolStripMenuItem(entry.Text) { Enabled = entry.Enabled };
            if (entry.Default)
            {
                item.Font = Theme.Ui(9.5f, FontStyle.Bold);
            }

            if (entry.Shield)
            {
                item.Image = Shield.Value;
            }

            var role = entry.Role;
            item.Click += (_, _) => Choose(role);
            _menu.Items.Add(item);
        }

        _menu.ResumeLayout();
    }

    private static readonly Lazy<Image> Shield = new(() => SystemIcons.GetStockIcon(StockIconId.Shield, StockIconOptions.SmallIcon).ToBitmap());

    private void Choose(TrayRole role)
    {
        switch (role)
        {
            case TrayRole.Open:
                OpenKiosk();
                break;
            case TrayRole.Info:
                ShowInfo();
                break;
            case TrayRole.ServiceStart or TrayRole.ServiceStop or TrayRole.ServiceRestart:
                _ = ServiceAsync(role);
                break;
            case TrayRole.Quit:
                Quit();
                break;
        }
    }

    private void OpenKiosk()
    {
        if (_kiosk is null || _kiosk.IsDisposed)
        {
            _kiosk = new KioskWindow();
            _kiosk.FormClosed += (_, _) => _kiosk = null;
            _kiosk.Show();
        }

        if (_kiosk.WindowState == FormWindowState.Minimized)
        {
            _kiosk.WindowState = FormWindowState.Normal;
        }

        _kiosk.Activate();
    }

    private void ShowInfo()
    {
        if (_info is null || _info.IsDisposed)
        {
            _info = new InfoWindow(_status, OpenKiosk);
            _info.FormClosed += (_, _) => _info = null;
            _info.Show();
        }

        _info.Activate();
    }

    /// <summary>
    /// Start, stop or restart the service, with administrator rights (the shield; the tray itself never runs elevated, SETUP-03). The answer is the state in the
    /// menu and in Info, never a notification (TRAY-04: the person did it).
    /// </summary>
    private async Task ServiceAsync(TrayRole role)
    {
        var command = role switch
        {
            TrayRole.ServiceStart => "Start-Service",
            TrayRole.ServiceStop => "Stop-Service -Force",
            _ => "Restart-Service -Force",
        };
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -NonInteractive -Command \"{command} -Name '{AgentInfo.ServiceName}'\"",
            };
            using var process = Process.Start(start);
            if (process is not null)
            {
                await process.WaitForExitAsync();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // the rights were refused: nothing changed
        }

        Apply(await Task.Run(ReadStatusAsync));
    }

    /// <summary>Quit (TRAY-01, AGENT-04): ends the tray, never the service; the kiosk and the dialogs come back with the next sign-in.</summary>
    private void Quit()
    {
        _stop = true;
        _poll.Stop();
        _tray.Visible = false;
        ExitThread();
    }

    /// <summary>The pictures of the tray for a test or the documentation: Info, the menu and the icon in each state (64 px).</summary>
    private void SnapTray(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var state in Enum.GetValues<TrayState>())
        {
            using var icon = TrayIcons.Draw(state, 64);
            icon.Save(Path.Combine(folder, $"tray-{state.ToString().ToLowerInvariant()}.png"), ImageFormat.Png);
        }

        using (var info = new InfoWindow(_status, () => { }))
        {
            UiApp.SnapWindow(info, Path.Combine(folder, "info.png"));
        }

        try
        {
            _menu.Show(new Point(-32000, -32000));
            Application.DoEvents();
            using var bitmap = new Bitmap(_menu.Width, _menu.Height);
            _menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, _menu.Size));
            bitmap.Save(Path.Combine(folder, "menu.png"), ImageFormat.Png);
        }
        finally
        {
            _menu.Close();
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop)
        {
            PipeResponse response;
            try
            {
                var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
                response = await UiPipe.RequestAsync(new PipeRequest { Op = "wait", User = user }, TimeSpan.FromSeconds(40));
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                // the service is not there (yet): ask again a little later
                await Task.Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            if (_stop || Replaced(response.Version))
            {
                return;
            }

            if (response.Dialog is not { } dialog)
            {
                continue;
            }

            // a dialog that cannot be shown must not end this loop (then no dialog would come any more until the next sign-in): the script hears "error"
            UiReply? reply;
            try
            {
                reply = await OnUi(() => Handle(dialog));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reply = new UiReply { Answer = "error" };
            }
            if (reply is not null && response.DialogId is { } id)
            {
                try
                {
                    await UiPipe.RequestAsync(new PipeRequest { Op = "answer", DialogId = id, Reply = reply }, TimeSpan.FromSeconds(15));
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
                {
                    // the service restarted: the script gets "timeout"
                }
            }
        }
    }

    /// <summary>The service runs another version (it updated itself): the installed program starts its tray, this one ends.</summary>
    private bool Replaced(string? version)
    {
        if (version is null || version == AgentInfo.Version || AgentInfo.OwnDataDirectory || _snapshots is not null
            || !string.Equals(Environment.ProcessPath, AgentInfo.InstalledExe, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _stop = true;
        Process.Start(new ProcessStartInfo(AgentInfo.InstalledExe) { ArgumentList = { "tray", "--after", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false });
        _ui.Post(_ =>
        {
            _poll.Stop();
            _tray.Visible = false;
            ExitThread();
        }, null);
        return true;
    }

    private Task<UiReply?> OnUi(Func<UiReply?> show)
    {
        var done = new TaskCompletionSource<UiReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try
            {
                done.SetResult(show());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    private UiReply? Handle(UiDialog dialog)
    {
        switch (dialog.Kind)
        {
            case UiKinds.Notify:
                if (_snapshots is null)
                {
                    _tray.ShowBalloonTip(8000, string.IsNullOrWhiteSpace(dialog.Title) ? "MatHub" : dialog.Title, dialog.Text ?? string.Empty, ToolTipIcon.Info);
                }

                return null;
            case UiKinds.Progress:
                _progress ??= new ProgressWindow();
                _progress.Set(dialog);
                if (_snapshots is not null)
                {
                    UiApp.Offscreen(_progress);
                    _progress.Show();
                    UiApp.Snap(_progress, Next("progress"));
                    _progress.Hide();
                }
                else
                {
                    _progress.Show();
                }

                return null;
            case "close":
                CloseProgress();
                return null;
        }

        CloseProgress();
        using var window = new DialogWindow(dialog);
        if (_auto)
        {
            if (_snapshots is not null)
            {
                UiApp.Offscreen(window);
                window.Show();
                for (var page = 0; page < window.Pages; page++)
                {
                    window.ShowPage(page);
                    UiApp.Snap(window, Next(dialog.Kind + (window.Pages > 1 ? "-" + (page + 1) : string.Empty)));
                }
            }

            var reply = window.AutoAnswer();
            window.Close();
            return reply;
        }

        window.ShowDialog();
        return window.Reply;
    }

    private void CloseProgress()
    {
        _progress?.Close();
        _progress?.Dispose();
        _progress = null;
    }

    private string Next(string name) => Path.Combine(_snapshots!, $"{++_shot:00}-{name}.png");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stop = true;
            _poll.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            TrayIcons.Release(_icon);
            _icon = null;
        }

        base.Dispose(disposing);
    }
}
