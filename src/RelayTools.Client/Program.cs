using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using RelayTools.Core;

var options = RunOptions.Parse(args);
using var telemetry = new Telemetry(options);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var monitor = telemetry.MonitorAsync(stop.Token);
var stopWatcher = Task.Run(async () =>
{
    try
    {
        while (!stop.IsCancellationRequested)
        {
            if (options.StopRequested) { stop.Cancel(); break; }
            await Task.Delay(100, stop.Token);
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
});
var peers = new List<(string Endpoint, int User, ClientWebSocket Socket)>();
var readers = new List<Task>();
try
{
    var endpoints = new[] { (Name: "kestrel", Port: options.KestrelPort), (Name: "httplistener", Port: options.HttpListenerPort) }
        .Where(e => options.Endpoints == "both" || options.Endpoints == e.Name).ToArray();
    foreach (var endpoint in endpoints)
    {
        for (var user = 0; user < options.Clients; user++)
        {
            var socket = new ClientWebSocket();
            peers.Add((endpoint.Name, user, socket));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{endpoint.Port}/chat?room={Uri.EscapeDataString(options.Room)}"), timeout.Token);
            Interlocked.Increment(ref telemetry.For(endpoint.Name).Active);
            readers.Add(Read(endpoint.Name, socket));
        }
    }
    // 兩端使用同一排程原點、序號與內容，互相不等待 SendAsync。
    var origin = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
    var warmupEnd = origin + (long)(options.Warmup * Stopwatch.Frequency);
    var phaseTask = Task.Run(async () =>
    {
        options.SetPhase("warmup");
        await Until(warmupEnd, stop.Token);
        options.SetPhase("measure");
    });
    await Task.WhenAll(peers.Select(p => Send(p.Endpoint, p.User, p.Socket, origin)));
    await phaseTask;
    options.SetPhase("drain");
    // 最後一包廣播送達後才發 close，避免把正常 drain 誤判為遺失。
    var drain = Stopwatch.StartNew();
    while (!stop.IsCancellationRequested && drain.Elapsed < TimeSpan.FromSeconds(5) &&
        endpoints.Any(e => Interlocked.Read(ref telemetry.For(e.Name).Received) < Interlocked.Read(ref telemetry.For(e.Name).Sent) * options.Clients))
        await Task.Delay(20, stop.Token);
    foreach (var endpoint in endpoints)
    {
        var c = telemetry.For(endpoint.Name);
        var expected = Interlocked.Read(ref c.Sent) * options.Clients;
        var received = Interlocked.Read(ref c.Received);
        telemetry.Event("broadcast_check", new { endpoint = endpoint.Name, expected, received, passed = received == expected });
        if (received != expected && !stop.IsCancellationRequested)
            telemetry.Error(endpoint.Name, new InvalidDataException("Broadcast count mismatch."));
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { telemetry.Event("cancelled"); }
catch (Exception ex) { telemetry.Error("client", ex); Environment.ExitCode = 1; }
finally
{
    options.SetPhase("closing");
    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    foreach (var p in peers)
    {
        try
        {
            if (p.Socket.State == WebSocketState.Open)
                await p.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Finished", closeTimeout.Token);
        }
        catch (Exception ex) { telemetry.Error(p.Endpoint, ex); }
    }
    try { await Task.WhenAll(readers).WaitAsync(closeTimeout.Token); }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
    {
        telemetry.Error("client", ex);
        foreach (var p in peers) p.Socket.Abort();
        await Task.WhenAll(readers);
    }
    foreach (var p in peers) p.Socket.Dispose();
    stop.Cancel();
    await Task.WhenAll(monitor, stopWatcher);
    if (peers.Any(p => Interlocked.Read(ref telemetry.For(p.Endpoint).Errors) != 0) || telemetry.For("client").Errors != 0)
        Environment.ExitCode = 1;
}

async Task Send(string endpoint, int user, ClientWebSocket socket, long origin)
{
    var bytes = new byte[options.PayloadBytes];
    Array.Fill(bytes, (byte)'x');
    BinaryPrimitives.WriteInt32LittleEndian(bytes, user);
    var count = telemetry.For(endpoint);
    var totalSeconds = options.Warmup + options.Duration;
    try
    {
        for (long sequence = 0; !stop.IsCancellationRequested; sequence++)
        {
            var seconds = sequence / options.Rate;
            if (options.Duration != 0 && seconds >= totalSeconds) break;
            var scheduled = origin + (long)(seconds * Stopwatch.Frequency);
            await Until(scheduled, stop.Token);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), scheduled);
            bytes[20] = (byte)(seconds < options.Warmup ? 0 : 1);
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, stop.Token);
            Interlocked.Increment(ref count.Sent);
        }
        if (options.Duration != 0) await Until(origin + (long)(totalSeconds * Stopwatch.Frequency), stop.Token);
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    catch (Exception ex) { telemetry.Error(endpoint, ex); stop.Cancel(); }
}

async Task Read(string endpoint, ClientWebSocket socket)
{
    var count = telemetry.For(endpoint);
    try
    {
        while (true)
        {
            var message = await ChatRoom.ReadAsync(socket, CancellationToken.None);
            if (message == null) break;
            var data = message.Value.Data;
            if (data.Length != options.PayloadBytes || data.Length < 24) throw new InvalidDataException("Invalid broadcast payload.");
            var scheduled = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(12));
            count.ReceivedMessage(data[20], Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds);
        }
    }
    catch (Exception ex) { telemetry.Error(endpoint, ex); }
    finally
    {
        Interlocked.Decrement(ref count.Active);
        Interlocked.Increment(ref count.Closed);
        telemetry.Event("connection_closed", new { endpoint, active = Interlocked.Read(ref count.Active) });
    }
}

static async Task Until(long timestamp, CancellationToken token)
{
    while (true)
    {
        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), timestamp);
        if (remaining <= TimeSpan.Zero) return;
        await Task.Delay(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining, token);
    }
}
