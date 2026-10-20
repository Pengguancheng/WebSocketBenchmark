using System.Collections.Concurrent;
using System.Net;
using RelayTools.Core;

// 讀取共用設定後固定本專案的角色，啟動資源監測與停止通知。
var options = RunOptions.Parse(args);
options.Mode = "httplistener";
using var telemetry = new Telemetry(options);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var monitor = telemetry.MonitorAsync(stop.Token);
var stopWatcher = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        if (options.StopRequested) { stop.Cancel(); break; }
        try { await Task.Delay(100, stop.Token); } catch (OperationCanceledException) { break; }
    }
});
// 聊天室訊息讀取、廣播與連線清理由共用核心處理。
var room = new ChatRoom(telemetry, options.Mode);
try
{
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{options.HttpListenerPort}/");
    listener.Start();
    File.WriteAllText(Path.Combine(options.Results, "httplistener.ready"), Environment.ProcessId.ToString());
    telemetry.Event("listening", new { address = $"ws://127.0.0.1:{options.HttpListenerPort}/chat" });
    var handlers = new ConcurrentDictionary<long, Task>();
    long nextId = 0;
    async Task Handle(HttpListenerContext context)
    {
        try
        {
            // 僅接受聊天室路徑的 WebSocket 連線，再交給相同聊天室核心。
            if (context.Request.Url?.AbsolutePath != "/chat" || !context.Request.IsWebSocketRequest)
            { context.Response.StatusCode = 400; context.Response.Close(); return; }
            var accepted = await context.AcceptWebSocketAsync(null);
            await room.HandleAsync(accepted.WebSocket, context.Request.QueryString["room"] ?? "", stop.Token);
        }
        catch (Exception ex) { telemetry.Error(options.Mode, ex); }
    }
    try
    {
        while (!stop.IsCancellationRequested)
        {
            var context = await listener.GetContextAsync().WaitAsync(stop.Token);
            var id = Interlocked.Increment(ref nextId);
            var task = Handle(context);
            handlers[id] = task;
            _ = task.ContinueWith(completed => handlers.TryRemove(id, out var removed), TaskScheduler.Default);
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    finally
    {
        // 停止監聽並等待已接受的連線處理工作完成。
        listener.Stop();
        await Task.WhenAll(handlers.Values).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
catch (Exception ex) { telemetry.Error(options.Mode, ex); Environment.ExitCode = 1; }
finally { stop.Cancel(); await Task.WhenAll(monitor, stopWatcher); }
