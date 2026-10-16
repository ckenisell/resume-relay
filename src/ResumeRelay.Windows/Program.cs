using System.Net.Sockets;

namespace ResumeRelay.Windows;

internal static class Program
{
    private const string SingleInstanceName = @"Local\ResumeRelay.SingleInstance";
    internal const string ShowWindowEventName = @"Local\ResumeRelay.ShowWindow";

    [STAThread]
    private static int Main(string[] args)
    {
        // One copy at a time. Launching it again just brings the running copy's window forward.
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceName, out bool firstInstance);
        if (!firstInstance)
        {
            try { using var show = EventWaitHandle.OpenExisting(ShowWindowEventName); show.Set(); } catch { }
            return 0;
        }

        ApplicationConfiguration.Initialize();

        string dataDir = RelayHost.DefaultDataDirectory();
        int port = RelayHost.DefaultPort();
        Directory.CreateDirectory(dataDir);

        var autostart = new WindowsAutostart();
        autostart.ApplyFirstRunDefault(dataDir);   // on by default, the first time only
        autostart.RepairPathIfMoved();             // keeps the Startup entry pointing at this exe

        var app = RelayHost.Build(Array.Empty<string>(), new RelayOptions(dataDir, port, autostart));
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsAddressInUse(ex))
        {
            MessageBox.Show(
                $"Resume Relay could not start because another program is already using port {port}.\n\n" +
                "If Resume Relay is installed as a service on this computer, it is already running there.",
                "Resume Relay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        bool startHidden = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        using (var tray = new TrayApplication(port, dataDir, autostart, startHidden))
            Application.Run(tray);

        try { app.StopAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }
        return 0;
    }

    private static bool IsAddressInUse(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } ||
                e is IOException && e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
