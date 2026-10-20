using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace RelayTools.Core;

/// <summary>以相同 NLog 設定記錄各程序資源與端點計數。</summary>
public class Telemetry : IDisposable
{
    private readonly RunOptions options;
    private readonly Logger logger;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, Counters> counters = new();
    private readonly Process process = Process.GetCurrentProcess();
    private double previousCpu;
    private double previousElapsed;

    public Telemetry(RunOptions options)
    {
        this.options = options;
        var config = new LoggingConfiguration();
        var target = new FileTarget("json")
        {
            FileName = Path.Combine(options.Results, $"{options.Mode}-{Environment.ProcessId}.jsonl"),
            Layout = "${message}", KeepFileOpen = true, AutoFlush = true
        };
        config.AddRule(LogLevel.Info, LogLevel.Fatal, target);
        LogManager.Configuration = config;
        logger = LogManager.GetLogger(options.Mode);
        Console.WriteLine($"mode={options.Mode} PID={Environment.ProcessId} config={options.ConfigPath} options={JsonSerializer.Serialize(options)}");
        Event("started", new { options, runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(), cpuCount = Environment.ProcessorCount });
    }

    public Counters For(string endpoint) => counters.GetOrAdd(endpoint, _ => new Counters());

    public void Event(string name, object? data = null) => logger.Info(JsonSerializer.Serialize(new
    {
        time = DateTimeOffset.UtcNow, runId = options.RunId, pid = Environment.ProcessId,
        mode = options.Mode, elapsedSeconds = elapsed.Elapsed.TotalSeconds, phase = options.Phase, @event = name, data
    }));

    public void Error(string endpoint, Exception error)
    {
        Interlocked.Increment(ref For(endpoint).Errors);
        Event("error", new { endpoint, type = error.GetType().Name, error.Message });
    }

    public void Sample()
    {
        process.Refresh();
        var cpu = process.TotalProcessorTime.TotalMilliseconds;
        var now = elapsed.Elapsed.TotalMilliseconds;
        var delta = cpu - previousCpu;
        var wall = now - previousElapsed;
        Event("metrics", new
        {
            cpuTotalMs = cpu, cpuDeltaMs = delta, intervalMs = wall,
            cpuOneCorePercent = wall > 0 ? delta / wall * 100 : 0,
            cpuMachinePercent = wall > 0 ? delta / wall * 100 / Environment.ProcessorCount : 0,
            cpuMachineDenominator = Environment.ProcessorCount,
            workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
            managedHeapBytes = GC.GetTotalMemory(false), gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2),
            endpoints = counters.ToDictionary(x => x.Key, x => x.Value.Snapshot())
        });
        previousCpu = cpu;
        previousElapsed = now;
    }

    public async Task MonitorAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(token)) Sample(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        Sample();
        Event("stopped");
        LogManager.Shutdown();
        process.Dispose();
    }
}

/// <summary>累計收發與依訊息階段區分的延遲。</summary>
public class Counters
{
    public long Sent, Received, Errors, Active, Closed;
    private readonly object gate = new();
    private long warmupReceived, measureReceived;
    private double warmupLatency, measureLatency, measureMax;

    public void ReceivedMessage(byte phase, double latencyMs)
    {
        Interlocked.Increment(ref Received);
        lock (gate)
        {
            if (phase == 0) { warmupReceived++; warmupLatency += latencyMs; }
            else { measureReceived++; measureLatency += latencyMs; measureMax = Math.Max(measureMax, latencyMs); }
        }
    }

    public object Snapshot()
    {
        lock (gate) return new
        {
            sent = Interlocked.Read(ref Sent), received = Interlocked.Read(ref Received), errors = Interlocked.Read(ref Errors),
            active = Interlocked.Read(ref Active), closed = Interlocked.Read(ref Closed), warmupReceived, measureReceived,
            warmupScheduledLatencyMeanMs = warmupReceived == 0 ? 0 : warmupLatency / warmupReceived,
            measureScheduledLatencyMeanMs = measureReceived == 0 ? 0 : measureLatency / measureReceived,
            measureScheduledLatencyMaxMs = measureMax
        };
    }
}
