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
    private readonly string WsUrl;
    private const int ReconnectDelayMs = 5000;
    private const int PingIntervalMs = 30000;
    private const int ReceiveBufferSize = 1024 * 128;
    private const int MaxMessageSizeBytes = 10 * 1024 * 1024; // 10 MB hard cap per message

    /// <summary>Shortest gap between two uploads of the same roster, so a server retry loop cannot spam it.</summary>
    private const long RosterUploadCooldownMs = 60000;

    private readonly DataService dataService;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource cts = new();
    private readonly object rosterUploadGate = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private Task? connectionTask;
    private ClientWebSocket? activeWs;
    private string? lastUploadedRosterHash;
    private long lastRosterUploadTick;
    private bool disposed;

    public bool IsConnected => activeWs?.State == WebSocketState.Open;

    /// Fired on the receive thread when an ACQ_RESULT message arrives.
    public event Action<AcqResultMessage>? OnAcqResult;

    /// <summary>
    /// Supplies the latest complete free company roster, or null when there is
    /// nothing to report. Called from the ping and receive threads, so the
    /// provider must hand back an already-built snapshot and never touch game
    /// memory itself.
    /// </summary>
    public Func<FcRosterSnapshot?>? FcRosterProvider { get; set; }

    public WebSocketService(DataService dataService, IPluginLog log, Configuration config)
    {
        this.dataService = dataService;
        this.log = log;
        WsUrl = config.WsServerAddress;
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

    public void SubmitEventParticipant(string eventId, string ingameName, string role, string? jobClass)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ws = activeWs;
                if (ws?.State != WebSocketState.Open)
                {
                    log.Warning("[RedMoonCappuccino] SubmitEventParticipant: not connected.");
                    return;
                }

                object input = string.IsNullOrWhiteSpace(jobClass)
                    ? new { eventId, ingameName, role }
                    : new { eventId, ingameName, role, @class = jobClass };

                await SendAsync(ws, new { type = "submit_event_participant", input }, cts.Token);
            }
            catch (Exception ex)
            {
                log.Warning($"[RedMoonCappuccino] SubmitEventParticipant failed: {ex.Message}");
            }
        });
    }

    public void RequestAcquisition(uint itemId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ws = activeWs;
                if (ws?.State == WebSocketState.Open)
                    await SendAsync(ws, new { type = "ACQ_QUERY", itemId = (int)itemId }, cts.Token);
            }
            catch (Exception ex)
            {
                log.Warning($"[RedMoonCappuccino] RequestAcquisition failed for item {itemId}: {ex.Message}");
            }
        });
    }

    // ── Free company roster ──────────────────────────────────────────────────

    /// <summary>
    /// The heartbeat, carrying the roster hash when one is available. A failure
    /// to read the snapshot degrades to a bare ping rather than killing the ping
    /// loop, which would take the connection down with it.
    /// </summary>
    private object BuildPing()
    {
        try
        {
            if (FcRosterProvider?.Invoke() is { } snapshot)
                return new { type = "ping", fc = new { id = snapshot.FcId, h = snapshot.Hash, n = snapshot.Count } };
        }
        catch (Exception ex)
        {
            log.Warning($"[RedMoonCappuccino] Reading FC roster for ping failed: {ex.Message}");
        }

        return new { type = "ping" };
    }

    /// <summary>
    /// Uploads the full roster, in response to the server reporting a hash it
    /// does not hold. The same hash is not re-sent within the cooldown, so a
    /// server stuck asking cannot turn this into a flood.
    /// </summary>
    private void SendRosterSnapshot(string? reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ws = activeWs;
                if (ws?.State != WebSocketState.Open) return;

                if (FcRosterProvider?.Invoke() is not { } snapshot)
                {
                    log.Debug("[RedMoonCappuccino] Roster requested but no complete snapshot is available yet.");
                    return;
                }

                var now = Environment.TickCount64;
                lock (rosterUploadGate)
                {
                    if (snapshot.Hash == lastUploadedRosterHash &&
                        now - lastRosterUploadTick < RosterUploadCooldownMs)
                        return;

                    lastUploadedRosterHash = snapshot.Hash;
                    lastRosterUploadTick   = now;
                }

                await SendAsync(ws, new
                {
                    type       = "fc_roster",
                    fcId       = snapshot.FcId,
                    fcName     = snapshot.FcName,
                    world      = snapshot.World,
                    worldId    = snapshot.WorldId,
                    hash       = snapshot.Hash,
                    n          = snapshot.Count,
                    reporter   = snapshot.Reporter,
                    capturedAt = snapshot.CapturedAt.ToString("o"),
                    m          = snapshot.Members,
                }, cts.Token);

                log.Information($"[RedMoonCappuccino] Sent FC roster ({snapshot.Count} members, hash {snapshot.Hash}, reason {reason ?? "unspecified"}).");
            }
            catch (Exception ex)
            {
                log.Warning($"[RedMoonCappuccino] Sending FC roster failed: {ex.Message}");
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

        // Per-connection CTS so the ping loop is always stopped when the
        // receive loop exits, regardless of the reason (close frame, error,
        // global cancellation, etc.).
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var connectionToken = connectionCts.Token;

        var pingTask = Task.Run(async () =>
        {
            while (!connectionToken.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try
                {
                    // Sent before the first delay so the roster hash reaches the
                    // server on connect rather than 30s later.
                    if (ws.State == WebSocketState.Open)
                        await SendAsync(ws, BuildPing(), connectionToken);
                    await Task.Delay(PingIntervalMs, connectionToken);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        });

        try
        {
            var buffer = new byte[ReceiveBufferSize];
            using var messageBuffer = new MemoryStream();

            while (!connectionToken.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;

                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), connectionToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                        return;
                    }

                    if (messageBuffer.Length + result.Count > MaxMessageSizeBytes)
                    {
                        log.Warning($"[RedMoonCappuccino] Message exceeds {MaxMessageSizeBytes / 1024 / 1024} MB limit — closing connection.");
                        await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None);
                        return;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                HandleMessage(text);
            }
        }
        finally
        {
            // Guarantee the ping loop stops and is awaited before we return,
            // even if we returned early (close frame, exception, cancellation).
            connectionCts.Cancel();
            try { await pingTask; } catch { }
        }
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

                case "ACQ_RESULT":
                    var acq = JsonSerializer.Deserialize<AcqResultMessage>(text);
                    if (acq != null)
                        OnAcqResult?.Invoke(acq);
                    break;

                case "pong":
                    break;

                case "fc_roster_request":
                    var req = JsonSerializer.Deserialize<FcRosterRequestMessage>(text);
                    SendRosterSnapshot(req?.Reason);
                    break;

                case "fc_roster_ack":
                    var rosterAck = JsonSerializer.Deserialize<FcRosterAckMessage>(text);
                    if (rosterAck is { Accepted: false })
                        log.Warning($"[RedMoonCappuccino] Server rejected FC roster: {rosterAck.Message ?? "no reason given"}");
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

    /// <summary>
    /// Serialises every outbound frame. ClientWebSocket allows only one send in
    /// flight at a time, and pings, image/event/acquisition requests and roster
    /// uploads all originate on their own tasks.
    /// </summary>
    private async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await sendGate.WaitAsync(ct);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            sendGate.Release();
        }
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
        sendGate.Dispose();
    }
}
