using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ResumeRelay.Windows;

/// <summary>
/// The app window: the same page the Linux edition serves, shown through WebView2 (the Edge engine built into
/// Windows 10 and 11). If WebView2 is missing, it offers to open the page in the default browser instead.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly string _url;
    private readonly string _dataDir;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public MainForm(int port, string dataDir, Icon icon)
    {
        _url = $"http://127.0.0.1:{port}/";
        _dataDir = dataDir;
        Text = "Resume Relay";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1240, 900);
        MinimumSize = new Size(420, 520);
        BackColor = Color.FromArgb(0x15, 0x1A, 0x2E);   // the page's background, so there is no white flash
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
        Load += async (_, _) => await StartBrowserAsync();
    }

    private async Task StartBrowserAsync()
    {
        try
        {
            // Keep WebView2's own files with ours, not next to the exe (which may be in a read-only folder).
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            // Any link that leaves the app opens in the user's normal browser.
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenInBrowser(e.Uri); };
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(_url, StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; OpenInBrowser(e.Uri); }
            };
            _web.Source = new Uri(_url);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException || ex.InnerException is WebView2RuntimeNotFoundException)
        {
            ShowFallback("This window needs Microsoft Edge WebView2, which isn't installed on this PC.");
        }
        catch (Exception ex)
        {
            ShowFallback("The app window could not start: " + ex.Message);
        }
    }

    private void ShowFallback(string reason)
    {
        Controls.Clear();
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(32), BackColor = BackColor,
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.FromArgb(0xEC, 0xE8, 0xF4),
            Font = new Font(Font.FontFamily, 11f), Margin = new Padding(0, 0, 0, 16),
            Text = reason + "\n\nResume Relay is still running and copying bookmarks. You can use it in your web browser instead.",
        });
        var open = new Button { Text = "Open in my browser", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        open.Click += (_, _) => OpenInBrowser(_url);
        var get = new Button { Text = "Get WebView2 from Microsoft", AutoSize = true, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 8, 0, 0) };
        get.Click += (_, _) => OpenInBrowser("https://developer.microsoft.com/microsoft-edge/webview2/");
        panel.Controls.Add(open);
        panel.Controls.Add(get);
        Controls.Add(panel);
    }

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
