namespace ResumeRelay.Windows;

/// <summary>
/// Owns the tray icon and the window. Closing the window only hides it: bookmarks keep syncing in the
/// background. "Quit" in the tray menu is the only way to stop the app.
/// </summary>
internal sealed class TrayApplication : ApplicationContext
{
    private readonly int _port;
    private readonly string _dataDir;
    private readonly WindowsAutostart _autostart;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _startWithWindows;
    private readonly SynchronizationContext _ui;
    private readonly EventWaitHandle _showSignal;
    private readonly RegisteredWaitHandle _showWait;
    private readonly Icon _icon;
    private MainForm? _window;
    private bool _quitting;
    private bool _toldAboutTray;

    public TrayApplication(int port, string dataDir, WindowsAutostart autostart, bool startHidden)
    {
        _port = port;
        _dataDir = dataDir;
        _autostart = autostart;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _icon = AppIcon.Load();
        _toldAboutTray = File.Exists(Path.Combine(dataDir, "windows-tray-hint.done"));

        var open = new ToolStripMenuItem("Open Resume Relay", null, (_, _) => ShowWindow()) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        _startWithWindows = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Checked = SafeGetAutostart() };
        var quit = new ToolStripMenuItem("Quit Resume Relay", null, (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Resume Relay — copying bookmarks between your Zidoo players",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip { Items = { open, _startWithWindows, new ToolStripSeparator(), quit } },
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        // The web page can change "Start with Windows" too; keep the tray tick in step.
        _autostart.Changed += enabled => _ui.Post(_ => _startWithWindows.Checked = enabled, null);

        // A second launch of the exe signals this event instead of starting another copy.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowWindowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => _ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        if (!startHidden) ShowWindow();
    }

    private void ShowWindow()
    {
        if (_quitting) return;
        if (_window == null || _window.IsDisposed)
        {
            _window = new MainForm(_port, _dataDir, _icon);
            _window.FormClosing += OnWindowClosing;
        }
        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, FormClosingEventArgs e)
    {
        if (_quitting || e.CloseReason == CloseReason.WindowsShutDown) return;
        e.Cancel = true;            // keep running; just hide
        _window!.Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            try { File.WriteAllText(Path.Combine(_dataDir, "windows-tray-hint.done"), ""); } catch { }
            _tray.ShowBalloonTip(6000, "Resume Relay is still running",
                "Bookmarks keep syncing in the background. Click the tray icon to open it, or right-click it to quit.",
                ToolTipIcon.Info);
        }
    }

    private void ToggleAutostart()
    {
        try { _autostart.SetAutostart(!_startWithWindows.Checked); }
        catch (Exception ex) { MessageBox.Show("Could not change the Windows startup setting: " + ex.Message, "Resume Relay"); }
    }

    private bool SafeGetAutostart()
    {
        try { return _autostart.GetAutostart(); } catch { return false; }
    }

    private void Quit()
    {
        _quitting = true;
        _window?.Close();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait.Unregister(null);
            _showSignal.Dispose();
            _tray.Dispose();
            _window?.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class AppIcon
{
    public static Icon Load()
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico");
        return s != null ? new Icon(s) : (Icon)SystemIcons.Application.Clone();
    }
}
