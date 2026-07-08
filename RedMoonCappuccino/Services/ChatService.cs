using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using RedMoonCappuccino.Models;

namespace RedMoonCappuccino.Services;

public enum ChatConnectionState
{
    /// <summary>No socket; idle. The default, "logged off" state.</summary>
    Disconnected,
    /// <summary>Socket opening or join not yet acknowledged.</summary>
    Connecting,
    /// <summary>Socket open and join acknowledged — fully logged in.</summary>
    Connected,
}

/// <summary>
/// Client for the live-chat WebSocket (chat-ws/v1, see docs/live_chat.md).
///
/// This is a <b>separate</b> connection from <see cref="WebSocketService"/> (the external-data
/// WS). Unlike that service it is <b>not</b> auto-started: it stays disconnected until the user
/// explicitly logs in via the chat window, and tears down on log off. There is no auto-reconnect —
/// a dropped connection returns to <see cref="ChatConnectionState.Disconnected"/> and the user
/// reconnects manually.
/// </summary>
public sealed class ChatService : IDisposable
{
    private const int PingIntervalMs = 30000;
    private const int ReceiveBufferSize = 1024 * 64;
    private const int MaxMessageSizeBytes = 4 * 1024 * 1024;
    private const int MaxHistory = 300;

    private readonly IPluginLog log;
    private readonly Configuration config;

    private readonly object gate = new();
    private readonly List<ChatMessage> messages = new();
    private List<ChatPresenceUser> presence = new();
    private string requestedUsername = string.Empty;

    // Direct-message conversations keyed by the peer's resolved username.
    private readonly Dictionary<string, List<ChatMessage>> dmConversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> dmUnread = new(StringComparer.OrdinalIgnoreCase);

    private ClientWebSocket? activeWs;
    private CancellationTokenSource? sessionCts;
    private Task? sessionTask;
    private bool disposed;

    // Enum reads/writes are atomic; volatile keeps the UI thread from seeing a stale value.
    private volatile ChatConnectionState state = ChatConnectionState.Disconnected;

    public ChatConnectionState State => state;
    public string ResolvedUsername { get; private set; } = string.Empty;
    public bool IsFCMember { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Last DM delivery problem (offline peer, unsupported server, ...); cleared on the next successful DM.</summary>
    public string? LastDmNotice { get; private set; }

    /// <summary>
    /// Raised when a direct message from another user arrives (never for the echo of our
    /// own DMs). Raised on the WS receive thread — marshal to the framework thread before
    /// touching UI or game state.
    /// </summary>
    public event Action<ChatMessage>? DmReceived;

    public ChatService(IPluginLog log, Configuration config)
    {
        this.log = log;
        this.config = config;
    }

    // ── Snapshots for the UI thread ───────────────────────────────────────────

    public List<ChatMessage> SnapshotMessages()
    {
        lock (gate) return new List<ChatMessage>(messages);
    }

    public List<ChatPresenceUser> SnapshotPresence()
    {
        lock (gate) return new List<ChatPresenceUser>(presence);
    }

    public int OnlineCount
    {
        get { lock (gate) return presence.Count; }
    }

    /// <summary>Usernames with an existing DM conversation this session.</summary>
    public List<string> SnapshotDmPeers()
    {
        lock (gate) return dmConversations.Keys.ToList();
    }

    /// <summary>Messages exchanged with <paramref name="peer"/> this session.</summary>
    public List<ChatMessage> SnapshotDm(string peer)
    {
        lock (gate)
            return dmConversations.TryGetValue(peer, out var list)
                ? new List<ChatMessage>(list)
                : new List<ChatMessage>();
    }

    public int GetDmUnread(string peer)
    {
        lock (gate) return dmUnread.GetValueOrDefault(peer);
    }

    public int TotalDmUnread
    {
        get { lock (gate) return dmUnread.Values.Sum(); }
    }

    public void MarkDmRead(string peer)
    {
        lock (gate) dmUnread.Remove(peer);
    }

    // ── Public control ────────────────────────────────────────────────────────

    /// <summary>
    /// Connect and join the room as the currently logged-in character. The username is
    /// captured from the game client rather than accepted from user input, so a session
    /// can only be opened under the local character's own name (no impersonation).
    /// Must be called from the main/draw thread (LocalPlayer access).
    /// </summary>
    public void Connect()
    {
        if (disposed) return;
        if (state != ChatConnectionState.Disconnected) return;

        var name = ((Plugin.ObjectTable.LocalPlayer as ICharacter)?.Name.ToString() ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            LastError = "Log in to a character first — the chat uses your character name.";
            return;
        }

        requestedUsername = name;
        LastError = null;
        LastDmNotice = null;
        state = ChatConnectionState.Connecting;

        lock (gate)
        {
            messages.Clear();
            presence = new List<ChatPresenceUser>();
            dmConversations.Clear();
            dmUnread.Clear();
        }

        sessionCts = new CancellationTokenSource();
        var token = sessionCts.Token;
        sessionTask = Task.Run(() => RunSession(token));
    }

    /// <summary>Log off — closes the socket and returns to the disconnected state.</summary>
    public void Disconnect()
    {
        var cts = sessionCts;
        sessionCts = null;
        try { cts?.Cancel(); }
        catch (Exception ex) { log.Warning($"[RedMoonCappuccino] Chat disconnect cancel failed: {ex.Message}"); }
    }

    /// <summary>Send a chat message. No-op unless fully connected.</summary>
    public void SendMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var ws = activeWs;
        var token = sessionCts?.Token ?? CancellationToken.None;
        if (state != ChatConnectionState.Connected || ws?.State != WebSocketState.Open) return;

        _ = Task.Run(async () =>
        {
            try { await SendAsync(ws, new { type = "message", text }, token); }
            catch (Exception ex) { log.Warning($"[RedMoonCappuccino] Chat send failed: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Send a direct message to <paramref name="to"/> (resolved username). No-op unless fully
    /// connected. The server echoes the DM back to the sender, so nothing is appended locally
    /// here — the conversation updates when the echo arrives.
    /// </summary>
    public void SendDirectMessage(string to, string text)
    {
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(text)) return;
        var ws = activeWs;
        var token = sessionCts?.Token ?? CancellationToken.None;
        if (state != ChatConnectionState.Connected || ws?.State != WebSocketState.Open) return;

        _ = Task.Run(async () =>
        {
            try { await SendAsync(ws, new { type = "dm", to, text }, token); }
            catch (Exception ex) { log.Warning($"[RedMoonCappuccino] Chat DM send failed: {ex.Message}"); }
        });
    }

    // ── Session ───────────────────────────────────────────────────────────────

    private async Task RunSession(CancellationToken token)
    {
        using var ws = new ClientWebSocket();
        activeWs = ws;

        try
        {
            log.Information($"[RedMoonCappuccino] Chat connecting to {config.ChatWsServerAddress}...");
            await ws.ConnectAsync(new Uri(config.ChatWsServerAddress), token);
            log.Information("[RedMoonCappuccino] Chat connected.");

            // Per-connection CTS so the ping loop always stops when receive exits.
            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pingTask = PingLoop(ws, connCts.Token);
            try
            {
                await ReceiveLoop(ws, connCts.Token);
            }
            finally
            {
                connCts.Cancel();
                try { await pingTask; } catch { /* expected on teardown */ }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: user logged off.
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.Warning($"[RedMoonCappuccino] Chat error: {ex.Message}");
        }
        finally
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }
            catch { /* best-effort close */ }

            activeWs = null;
            ResolvedUsername = string.Empty;
            IsFCMember = false;
            state = ChatConnectionState.Disconnected;
            lock (gate) presence = new List<ChatPresenceUser>();
            log.Information("[RedMoonCappuccino] Chat session ended.");
        }
    }

    private async Task PingLoop(ClientWebSocket ws, CancellationToken token)
    {
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(PingIntervalMs, token);
                if (ws.State == WebSocketState.Open)
                    await SendAsync(ws, new { type = "ping" }, token);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var messageBuffer = new MemoryStream();

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            messageBuffer.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    return;
                }

                if (messageBuffer.Length + result.Count > MaxMessageSizeBytes)
                {
                    log.Warning($"[RedMoonCappuccino] Chat message exceeds {MaxMessageSizeBytes / 1024 / 1024} MB — closing.");
                    await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None);
                    return;
                }

                messageBuffer.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);

            // A failed join means we will never become usable — stop the loop and tear down.
            if (!await HandleMessage(text, ws, token))
                return;
        }
    }

    /// <summary>Returns false to signal the receive loop to stop (e.g. fatal join error).</summary>
    private async Task<bool> HandleMessage(string text, ClientWebSocket ws, CancellationToken token)
    {
        WsMessage? raw;
        try { raw = JsonSerializer.Deserialize<WsMessage>(text); }
        catch (Exception ex)
        {
            log.Warning($"[RedMoonCappuccino] Chat: bad frame: {ex.Message}");
            return true;
        }
        if (raw == null) return true;

        switch (raw.Type)
        {
            case "hello":
                // Claim our username now that the server is ready for a join.
                await SendAsync(ws, new { type = "join", username = requestedUsername }, token);
                break;

            case "joined":
                var joined = JsonSerializer.Deserialize<ChatJoinedMessage>(text);
                ResolvedUsername = string.IsNullOrEmpty(joined?.Username) ? requestedUsername : joined!.Username;
                IsFCMember = joined?.IsFCMember ?? false;
                LastError = null;
                state = ChatConnectionState.Connected;
                break;

            case "history":
                var hist = JsonSerializer.Deserialize<ChatHistoryMessage>(text);
                if (hist?.Messages != null)
                {
                    lock (gate)
                    {
                        messages.Clear();
                        messages.AddRange(hist.Messages);
                        Trim();
                    }
                }
                break;

            case "presence":
                var pres = JsonSerializer.Deserialize<ChatPresenceMessage>(text);
                if (pres?.Users != null)
                    lock (gate) presence = pres.Users;
                break;

            case "message":
                var env = JsonSerializer.Deserialize<ChatMessageEnvelope>(text);
                if (env?.Message != null)
                    lock (gate) { messages.Add(env.Message); Trim(); }
                break;

            case "dm":
                var dmEnv = JsonSerializer.Deserialize<ChatMessageEnvelope>(text);
                if (dmEnv?.Message is { } dm)
                {
                    // The server echoes DMs to both parties; the conversation is keyed
                    // by whichever end of the exchange is not us.
                    var fromSelf = string.Equals(dm.Username, ResolvedUsername, StringComparison.OrdinalIgnoreCase);
                    var peer = fromSelf ? dm.To : dm.Username;
                    if (!string.IsNullOrEmpty(peer))
                    {
                        lock (gate)
                        {
                            if (!dmConversations.TryGetValue(peer, out var conv))
                                dmConversations[peer] = conv = new List<ChatMessage>();
                            conv.Add(dm);
                            if (conv.Count > MaxHistory)
                                conv.RemoveRange(0, conv.Count - MaxHistory);
                            if (!fromSelf)
                                dmUnread[peer] = dmUnread.GetValueOrDefault(peer) + 1;
                        }
                        LastDmNotice = null;

                        if (!fromSelf)
                        {
                            try { DmReceived?.Invoke(dm); }
                            catch (Exception ex) { log.Warning($"[RedMoonCappuccino] Chat DM handler failed: {ex.Message}"); }
                        }
                    }
                }
                break;

            case "system":
                var sys = JsonSerializer.Deserialize<ChatSystemMessage>(text);
                if (sys != null)
                    lock (gate) { messages.Add(ToNotice(sys)); Trim(); }
                break;

            case "left":
            case "pong":
                break;

            case "error":
                var err = JsonSerializer.Deserialize<ChatErrorMessage>(text);
                LastError = !string.IsNullOrEmpty(err?.Message) ? err!.Message
                          : !string.IsNullOrEmpty(err?.Code) ? err!.Code
                          : "Unknown server error";
                log.Warning($"[RedMoonCappuccino] Chat server error: {LastError}");
                // A rejected join (taken name, unresolved, etc.) is terminal for this session.
                if (string.Equals(err?.RequestType, "join", StringComparison.OrdinalIgnoreCase))
                    return false;
                // DM failures surface in the DM pane rather than the header. Old servers
                // answer an unknown "dm" type with unsupported_type and no requestType.
                if (string.Equals(err?.RequestType, "dm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(err?.Code, "unsupported_type", StringComparison.OrdinalIgnoreCase))
                {
                    LastDmNotice = string.Equals(err?.Code, "unsupported_type", StringComparison.OrdinalIgnoreCase)
                        ? "The chat server does not support direct messages yet."
                        : LastError;
                }
                break;

            default:
                log.Debug($"[RedMoonCappuccino] Chat: unknown type '{raw.Type}'");
                break;
        }

        return true;
    }

    private static ChatMessage ToNotice(ChatSystemMessage sys)
    {
        var verb = sys.Event switch
        {
            "user_joined" => "joined the room",
            "user_left"   => "left the room",
            _             => sys.Event,
        };
        return new ChatMessage
        {
            Username = sys.Username,
            Text = $"{sys.Username} {verb}",
            Ts = sys.Ts,
            IsSystem = true,
        };
    }

    /// <summary>Caps in-memory history. Caller must hold <see cref="gate"/>.</summary>
    private void Trim()
    {
        if (messages.Count > MaxHistory)
            messages.RemoveRange(0, messages.Count - MaxHistory);
    }

    private static async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { sessionCts?.Cancel(); } catch { }
        try { sessionTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { log.Warning($"[RedMoonCappuccino] Chat dispose wait: {ex.Message}"); }
        try { sessionCts?.Dispose(); } catch { }
    }
}
