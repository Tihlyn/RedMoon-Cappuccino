using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using RedMoonCappuccino.Models;

namespace RedMoonCappuccino.Services;

public class WebSocketService : IDisposable
{
    private const string WsUrl = "ws://78.116.140.30:3100";
    private const int ReconnectDelayMs = 5000;
    private const int PingIntervalMs = 30000;
    private const int ReceiveBufferSize = 1024 * 128;

    private readonly DataService dataService;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource cts = new();
    private Task? connectionTask;
    private ClientWebSocket? activeWs;
    private bool disposed;

    public bool IsConnected => activeWs?.State == WebSocketState.Open;

    public WebSocketService(DataService dataService, IPluginLog log)
    {
        this.dataService = dataService;
        this.log = log;
    }

    public void Start()
    {
        connectionTask = Task.Run(RunConnectionLoop);
    }

    // ── Image / Event request methods ────────────────────────────────────────

    public void RequestImage(string eventId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ws = activeWs;
                if (ws?.State == WebSocketState.Open)
                    await SendAsync(ws, new { type = "get_image", eventId }, cts.Token);
            }
            catch (Exception ex)
            {
                log.Warning($"[RedMoonCappuccino] RequestImage failed for {eventId}: {ex.Message}");
            }
        });
    }

    public void RequestEvent(string eventId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ws = activeWs;
                if (ws?.State == WebSocketState.Open)
                    await SendAsync(ws, new { type = "get_event", eventId }, cts.Token);
            }
            catch (Exception ex)
            {
                log.Warning($"[RedMoonCappuccino] RequestEvent failed for {eventId}: {ex.Message}");
            }
        });
    }

    // ── Connection loop ──────────────────────────────────────────────────────

    private async Task RunConnectionLoop()
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceive();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warning($"[RedMoonCappuccino] WebSocket error: {ex.Message}");
            }

            activeWs = null;

            if (cts.Token.IsCancellationRequested)
                break;

            log.Information($"[RedMoonCappuccino] Reconnecting in {ReconnectDelayMs / 1000}s...");
            try
            {
                await Task.Delay(ReconnectDelayMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        log.Information("[RedMoonCappuccino] Connection loop stopped.");
    }

    private async Task ConnectAndReceive()
    {
        using var ws = new ClientWebSocket();
        activeWs = ws;

        log.Information($"[RedMoonCappuccino] Connecting to {WsUrl}...");
        await ws.ConnectAsync(new Uri(WsUrl), cts.Token);
        log.Information("[RedMoonCappuccino] Connected.");

        // Start keepalive ping task
        var pingTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try
                {
                    await Task.Delay(PingIntervalMs, cts.Token);
                    if (ws.State == WebSocketState.Open)
                        await SendAsync(ws, new { type = "ping" }, cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        });

        var buffer = new byte[ReceiveBufferSize];
        using var messageBuffer = new MemoryStream();

        while (!cts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            messageBuffer.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    return;
                }

                messageBuffer.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
            HandleMessage(text);
        }

        await pingTask;
    }

    private void HandleMessage(string text)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<WsMessage>(text);
            if (raw == null) return;

            switch (raw.Type)
            {
                case "hello":
                    log.Information("[RedMoonCappuccino] Server hello received.");
                    break;

                case "snapshot":
                    var snap = JsonSerializer.Deserialize<SnapshotMessage>(text);
                    if (snap != null)
                        dataService.UpdateSnapshot(snap);
                    break;

                case "event":
                    // Full event detail not currently needed beyond what snapshot provides.
                    break;

                case "image":
                    var img = JsonSerializer.Deserialize<ImageResponseMessage>(text);
                    if (img != null)
                        dataService.UpdateImage(img);
                    break;

                case "pong":
                    break;

                case "error":
                    log.Warning($"[RedMoonCappuccino] Server error: {text}");
                    break;

                default:
                    log.Debug($"[RedMoonCappuccino] Unknown message type: {raw.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[RedMoonCappuccino] Failed to handle message: {ex.Message}");
        }
    }

    private static async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        try { connectionTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { log.Warning($"[RedMoonCappuccino] Exception during dispose wait: {ex.Message}"); }
        cts.Dispose();
    }
}
