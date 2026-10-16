using ResumeRelay;

// Linux edition: no window; systemd starts it with the computer and restarts it if it stops.
var options = new RelayOptions(RelayHost.DefaultDataDirectory(), RelayHost.DefaultPort(), new ServiceHostIntegration());
var app = RelayHost.Build(args, options);
Console.WriteLine($"Resume Relay {RelayHost.Version} — open http://<this-computer>:{options.Port} in a browser. Data: {options.DataDirectory}");
app.Run();
