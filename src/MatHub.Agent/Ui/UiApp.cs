using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MatHub.Protocol;

namespace MatHub.Agent.Ui;

/// <summary>
/// The windows of the agent in the session of the person signed in:
///   mathub-agent tray     the symbol beside the clock (started at every sign-in): opens the kiosk and shows the dialogs of scripts
///   mathub-agent kiosk    the kiosk window (the start menu entry "MatHub Kiosk")
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

    public static int Run(string mode, string[] args)
    {
        string? Option(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var snapshot = Option("--snapshot");
        var auto = args.Contains("--auto") || snapshot is not null;
        var exit = 0;
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            exit = mode == "tray" ? Tray(auto, snapshot, Option("--after")) : Kiosk(snapshot);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
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
/// The symbol beside the clock: opens the kiosk (double-click, menu) and shows what the scripts of the agent ask: it waits on the pipe of the service for a
/// dialog, shows it and gives back the answer. A newer agent (after an update of the service) starts its own tray and this one ends.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly SynchronizationContext _ui;
    private readonly bool _auto;
    private readonly string? _snapshots;
    private ProgressWindow? _progress;
    private KioskWindow? _kiosk;
    private int _shot;
    private volatile bool _stop;

    public TrayApp(bool auto, string? snapshots)
    {
        _auto = auto;
        _snapshots = snapshots;
        _ui = SynchronizationContext.Current!;
        var menu = new ContextMenuStrip { Font = Theme.Ui(9.5f), ShowImageMargin = false };
        var open = new ToolStripMenuItem(UiText.T("OpenKiosk"), null, (_, _) => OpenKiosk()) { Font = Theme.Ui(9.5f, FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(UiText.T("About", AgentInfo.Version)) { Enabled = false });
        _tray = new NotifyIcon
        {
            Icon = new Icon(UiApp.Icon, SystemInformation.SmallIconSize),
            Text = UiText.T("KioskTitle"),
            ContextMenuStrip = menu,
            Visible = snapshots is null,
        };
        _tray.DoubleClick += (_, _) => OpenKiosk();
        _tray.BalloonTipClicked += (_, _) => OpenKiosk();
        _ = Task.Run(LoopAsync);
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

            if (Replaced(response.Version))
            {
                return;
            }

            if (response.Dialog is not { } dialog)
            {
                continue;
            }

            var reply = await OnUi(() => Handle(dialog));
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
            _tray.Visible = false;
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
