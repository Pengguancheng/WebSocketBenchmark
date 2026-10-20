using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace RelayTools.Core;

/// <summary>共用完整訊息讀取、房間廣播及連線生命週期。</summary>
public class ChatRoom(Telemetry telemetry, string endpoint)
{
    private readonly ConcurrentDictionary<Guid, Peer> peers = new();

    public async Task HandleAsync(WebSocket socket, string room, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var peer = new Peer(socket, room);
        var count = telemetry.For(endpoint);
        peers[id] = peer;
        Interlocked.Increment(ref count.Active);
        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var message = await ReadAsync(socket, token);
                if (message == null) break;
                Interlocked.Increment(ref count.Received);
                await Task.WhenAll(peers.Values.Where(p => p.Room == room).Select(async p =>
                {
                    try
                    {
                        await p.SendGate.WaitAsync(token);
                        try
                        {
                            if (p.Socket.State != WebSocketState.Open) return;
                            await p.Socket.SendAsync(message.Value.Data, message.Value.Type, true, token);
                            Interlocked.Increment(ref count.Sent);
                        }
                        finally { p.SendGate.Release(); }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (WebSocketException ex) { telemetry.Error(endpoint, ex); p.Socket.Abort(); }
                }));
            }
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await peer.SendGate.WaitAsync(closeTimeout.Token);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeTimeout.Token);
            }
            finally { peer.SendGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { telemetry.Error(endpoint, ex); }
        finally
        {
            peers.TryRemove(id, out _);
            socket.Dispose();
            Interlocked.Decrement(ref count.Active);
            Interlocked.Increment(ref count.Closed);
            telemetry.Event("connection_closed", new { endpoint, room, active = Interlocked.Read(ref count.Active) });
        }
    }

    public static async Task<(byte[] Data, WebSocketMessageType Type)?> ReadAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (message.Length + result.Count > 1048576) throw new InvalidDataException("Message exceeds 1 MiB.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (message.ToArray(), result.MessageType);
    }

    private record Peer(WebSocket Socket, string Room)
    {
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }
}
