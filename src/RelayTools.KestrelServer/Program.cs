using RelayTools.Core;

// 讀取共用設定後固定本專案的角色，啟動資源監測與停止通知。
var options = RunOptions.Parse(args);
options.Mode = "kestrel";
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
    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls($"http://127.0.0.1:{options.KestrelPort}");
    await using var app = builder.Build();
    app.UseWebSockets();
    app.Run(async context =>
    {
        // 僅接受聊天室路徑的 WebSocket 連線，再交給相同聊天室核心。
        if (context.Request.Path != "/chat" || !context.WebSockets.IsWebSocketRequest)
        { context.Response.StatusCode = 400; return; }
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        await room.HandleAsync(socket, context.Request.Query["room"].ToString(), stop.Token);
    });
    await app.StartAsync(stop.Token);
    File.WriteAllText(Path.Combine(options.Results, "kestrel.ready"), Environment.ProcessId.ToString());
    telemetry.Event("listening", new { address = $"ws://127.0.0.1:{options.KestrelPort}/chat" });
    try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
    // 停止監聽並等待既有請求結束。
    await app.StopAsync(TimeSpan.FromSeconds(5));
}
catch (Exception ex) { telemetry.Error(options.Mode, ex); Environment.ExitCode = 1; }
finally { stop.Cancel(); await Task.WhenAll(monitor, stopWatcher); }
