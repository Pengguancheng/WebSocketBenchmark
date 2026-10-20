using System.Text.Json;

namespace RelayTools.Core;

/// <summary>三個程序共用的執行參數。</summary>
public class RunOptions
{
    public string Mode { get; set; } = "client";
    public int ClientsPerServer { get; set; } = 100;
    public double MessagesPerSecondPerClient { get; set; } = 1;
    public int PayloadBytes { get; set; } = 256;
    public double WarmupSeconds { get; set; } = 2;
    public double DurationSeconds { get; set; }
    public int KestrelPort { get; set; } = 5081;
    public int HttpListenerPort { get; set; } = 5082;
    public string Room { get; set; } = "lobby";
    public string Endpoints { get; set; } = "both";
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string Results { get; set; } = "results/manual";
    public string ConfigPath { get; set; } = "";
    public int Clients => ClientsPerServer;
    public double Rate => MessagesPerSecondPerClient;
    public double Warmup => WarmupSeconds;
    public double Duration => DurationSeconds;
    public string Phase => new[] { "closing", "drain", "measure", "warmup" }.FirstOrDefault(p => File.Exists(Path.Combine(Results, p + ".phase"))) ?? "setup";
    public bool StopRequested => File.Exists(Path.Combine(Results, "stop.txt"));
    public void SetPhase(string phase) => File.WriteAllText(Path.Combine(Results, phase + ".phase"), phase);

    public static RunOptions Parse(string[] args)
    {
        var configIndex = Array.IndexOf(args, "--config");
        var configPath = configIndex >= 0 && configIndex + 1 < args.Length
            ? Path.GetFullPath(args[configIndex + 1]) : FindConfig();
        var o = JsonSerializer.Deserialize<RunOptions>(File.ReadAllText(configPath))
            ?? throw new InvalidDataException("Invalid configuration.");
        o.ConfigPath = configPath;
        o.Mode = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name switch
        {
            "RelayTools.KestrelServer" => "kestrel",
            "RelayTools.HttpListenerServer" => "httplistener",
            _ => "client"
        };
        if (!Path.IsPathRooted(o.Results)) o.Results = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, o.Results));
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length) throw new ArgumentException("Options require a value.");
            var v = args[i + 1];
            switch (args[i])
            {
                case "--config": break;
                case "--mode": o.Mode = v; break;
                case "--clients": o.ClientsPerServer = int.Parse(v); break;
                case "--rate": o.MessagesPerSecondPerClient = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--payload-bytes": o.PayloadBytes = int.Parse(v); break;
                case "--warmup": o.WarmupSeconds = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--duration": o.DurationSeconds = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--kestrel-port": o.KestrelPort = int.Parse(v); break;
                case "--httplistener-port": o.HttpListenerPort = int.Parse(v); break;
                case "--room": o.Room = v; break;
                case "--endpoints": o.Endpoints = v; break;
                case "--run-id": o.RunId = v; break;
                case "--results": o.Results = Path.GetFullPath(v); break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (o.Clients < 1 || !double.IsFinite(o.Rate) || o.Rate <= 0 || o.PayloadBytes < 24 || o.PayloadBytes > 1048576 ||
            !double.IsFinite(o.Warmup) || o.Warmup < 0 || !double.IsFinite(o.Duration) || o.Duration < 0 ||
            o.KestrelPort is < 1 or > 65535 || o.HttpListenerPort is < 1 or > 65535 || o.KestrelPort == o.HttpListenerPort ||
            string.IsNullOrWhiteSpace(o.Room) || o.Endpoints is not ("both" or "kestrel" or "httplistener") ||
            o.Mode is not ("client" or "kestrel" or "httplistener"))
            throw new ArgumentException("Invalid run options.");
        Directory.CreateDirectory(o.Results);
        return o;
    }

    private static string FindConfig()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "relaytools.json");
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("relaytools.json was not found. Pass --config with an absolute path.");
    }
}
