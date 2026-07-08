using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RedMoonCappuccino.Models;
using RedMoonCappuccino.Services;
using RedMoonCappuccino.UI;

namespace RedMoonCappuccino.Windows;

/// <summary>
/// Standalone live-chat window. Opened via the <c>/livechat</c> command. Defaults to a
/// logged-off state; the user logs in/off against the chat WS with the header button.
/// Members list on the left; the right side is tabbed — the shared room plus one tab per
/// open direct-message conversation. Clicking an online member opens a DM tab.
/// </summary>
public sealed class ChatWindow : ThemedWindow, IDisposable
{
    private readonly ChatService chat;
    private readonly Configuration config;

    private string inputBuf = string.Empty;
    private int lastMessageCount;
    private bool focusInput;
    private long lastDrawnTick;

    // Preset steps for the room/DM font-size selectors.
    private static readonly float[] FontScaleSteps = { 0.8f, 0.9f, 1.0f, 1.1f, 1.25f, 1.5f };

    // Direct messages — open tabs (insertion order), per-peer input buffers and scroll state.
    private readonly List<string> openDmPeers = new();
    private readonly Dictionary<string, string> dmInputBufs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> dmLastCounts = new(StringComparer.OrdinalIgnoreCase);
    private string? pendingDmFocus;   // tab to select on next frame (member clicked)
    private string? focusDmInputPeer; // DM input to focus on next frame

    // FC member = cornflower accent; FC friend (non-FC) = light steel.
    private static readonly Vector4 FcColor     = RmcTheme.Cornflower;
    private static readonly Vector4 FriendColor = RmcTheme.LightSteel;
    private static readonly Vector4 MutedColor  = RmcTheme.TextMuted;
    private static readonly Vector4 SystemColor = RmcTheme.TextMuted;
    private static readonly Vector4 ErrorColor  = RmcTheme.Error;

    public ChatWindow(ChatService chat, Configuration config)
        : base("Live Chat###RmcChatWindow")
    {
        this.chat = chat;
        this.config = config;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 320),
            MaximumSize = new Vector2(1200, 1000),
        };
        Size          = new Vector2(620, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        base.PreDraw();

        // Unread-DM badge in the title; "###" keeps the window ID stable.
        var unread = chat.TotalDmUnread;
        WindowName = unread > 0
            ? $"Live Chat ({unread})###RmcChatWindow"
            : "Live Chat###RmcChatWindow";
    }

    /// <summary>
    /// True while the window body is on screen and focused — i.e. the user can actually
    /// see incoming messages. Collapsed or hidden windows do not run <see cref="Draw"/>,
    /// so a stale draw tick means the body is not visible even though the window is open.
    /// </summary>
    public bool IsActivelyViewed =>
        IsOpen && IsFocused && Environment.TickCount64 - lastDrawnTick < 250;

    public override void Draw()
    {
        lastDrawnTick = Environment.TickCount64;
        DrawHeader();
        ImGui.Separator();

        if (chat.State != ChatConnectionState.Connected)
        {
            DrawDisconnectedBody();
            return;
        }

        var avail     = ImGui.GetContentRegionAvail();
        var sidebarW  = 175f * ImGuiHelpers.GlobalScale;

        using (var members = ImRaii.Child("##members", new Vector2(sidebarW, avail.Y), true))
        {
            if (members) DrawMembers();
        }

        ImGui.SameLine();

        using (var pane = ImRaii.Child("##chatpane", new Vector2(0, avail.Y), false))
        {
            if (pane) DrawTabbedPanes();
        }
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        switch (chat.State)
        {
            case ChatConnectionState.Connected:
            {
                var tag = chat.IsFCMember ? "FC" : "Friend";
                RmcTheme.StatusDot(RmcTheme.Success,
                    $"{chat.ResolvedUsername}  [{tag}]",
                    chat.IsFCMember ? FcColor : FriendColor);
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                    ImGui.TextUnformatted($"· {chat.OnlineCount} online");

                ImGui.SameLine();
                RightAlignButton("Log Off");
                if (ImGui.Button("Log Off##chat"))
                    chat.Disconnect();
                break;
            }

            case ChatConnectionState.Connecting:
            {
                var spinner = "|/-\\"[(int)(ImGui.GetTime() * 4) % 4];
                using (ImRaii.PushColor(ImGuiCol.Text, FriendColor))
                    ImGui.TextUnformatted($"Connecting… {spinner}");

                ImGui.SameLine();
                RightAlignButton("Cancel");
                if (ImGui.Button("Cancel##chat"))
                    chat.Disconnect();
                break;
            }

            default: // Disconnected
            {
                RmcTheme.StatusDot(MutedColor, "Offline", MutedColor);
                ImGui.SameLine();

                // The chat identity is locked to the logged-in character — the field is
                // display-only, so no other name can be logged in (no impersonation).
                var name    = LocalCharacterName();
                var display = name ?? "No character logged in";
                ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
                using (ImRaii.PushColor(ImGuiCol.Text, name != null ? FriendColor : MutedColor))
                    ImGui.InputText("##chatuser", ref display, 64, ImGuiInputTextFlags.ReadOnly);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Chat identity is locked to your logged-in character.");

                ImGui.SameLine();
                using (ImRaii.Disabled(name == null))
                {
                    if (ImGui.Button("Log In##chat"))
                        chat.Connect();
                }
                break;
            }
        }

        if (!string.IsNullOrEmpty(chat.LastError) && chat.State != ChatConnectionState.Connected)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ErrorColor))
                ImGui.TextWrapped(chat.LastError);
        }
    }

    private void DrawDisconnectedBody()
    {
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
        {
            ImGui.TextWrapped(
                "You are logged off. Press \"Log In\" to join the live chat as your " +
                "current character.");
            ImGui.Spacing();
            ImGui.TextWrapped(
                "The chat always uses the name of the character you are playing. It is " +
                "resolved against the FC roster — both FC members and FC friends can join.");
        }
    }

    // ── Members pane ──────────────────────────────────────────────────────────

    private void DrawMembers()
    {
        using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
            ImGui.TextUnformatted($"Online — {chat.OnlineCount}");
        ImGui.Separator();
        ImGui.Spacing();

        var users = chat.SnapshotPresence();
        if (users.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.TextUnformatted("(nobody here)");
            return;
        }

        foreach (var u in users)
        {
            var isMe   = string.Equals(u.Username, chat.ResolvedUsername, StringComparison.Ordinal);
            var unread = chat.GetDmUnread(u.Username);
            var label  = isMe       ? $"● {u.Username} (you)"
                       : unread > 0 ? $"● {u.Username} ({unread})"
                       :              $"● {u.Username}";

            using (ImRaii.PushColor(ImGuiCol.Text, u.IsFCMember ? FcColor : FriendColor))
            {
                if (ImGui.Selectable($"{label}###member_{u.Username}", false) && !isMe)
                    OpenDm(u.Username);
            }

            if (!isMe && ImGui.IsItemHovered())
                ImGui.SetTooltip($"Open a direct message with {u.Username}");
        }
    }

    // ── Tabbed panes: room + direct messages ─────────────────────────────────

    private void DrawTabbedPanes()
    {
        // Auto-open a tab when a new DM arrives for a conversation that has no tab yet.
        // Only unread conversations reopen, so closing a quiet tab sticks.
        foreach (var peer in chat.SnapshotDmPeers())
        {
            if (chat.GetDmUnread(peer) > 0 &&
                !openDmPeers.Contains(peer, StringComparer.OrdinalIgnoreCase))
                openDmPeers.Add(peer);
        }

        using var bar = ImRaii.TabBar("##chatTabs");
        if (!bar) return;

        if (ImGui.BeginTabItem("Room###roomTab"))
        {
            DrawRoomPane();
            ImGui.EndTabItem();
        }

        for (var i = 0; i < openDmPeers.Count;)
        {
            var peer = openDmPeers[i];
            var open = true;

            var flags = ImGuiTabItemFlags.None;
            if (string.Equals(pendingDmFocus, peer, StringComparison.OrdinalIgnoreCase))
                flags |= ImGuiTabItemFlags.SetSelected;
            if (chat.GetDmUnread(peer) > 0)
                flags |= ImGuiTabItemFlags.UnsavedDocument; // unread indicator dot

            if (ImGui.BeginTabItem($"@ {peer}###dm_{peer}", ref open, flags))
            {
                if (string.Equals(pendingDmFocus, peer, StringComparison.OrdinalIgnoreCase))
                    pendingDmFocus = null;
                chat.MarkDmRead(peer);
                DrawDmPane(peer);
                ImGui.EndTabItem();
            }

            if (!open)
                openDmPeers.RemoveAt(i);
            else
                i++;
        }
    }

    // ── Room pane ─────────────────────────────────────────────────────────────

    private void DrawRoomPane()
    {
        var avail       = ImGui.GetContentRegionAvail();
        var inputHeight = ImGui.GetFrameHeightWithSpacing();

        using (var hist = ImRaii.Child("##history", new Vector2(0, avail.Y - inputHeight), true))
        {
            if (hist) DrawHistory();
        }

        DrawInputRow();
    }

    private void DrawHistory()
    {
        ImGui.SetWindowFontScale(config.ChatRoomFontScale);

        var msgs = chat.SnapshotMessages();

        foreach (var m in msgs)
            DrawMessageLine(m);

        // Keep pinned to the bottom when new messages arrive (or on first fill).
        if (msgs.Count != lastMessageCount)
        {
            lastMessageCount = msgs.Count;
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - ImGui.GetTextLineHeight())
                ImGui.SetScrollHereY(1f);
        }
    }

    private void DrawInputRow()
    {
        var scale = DrawFontScaleCombo("##roomFontScale", config.ChatRoomFontScale);
        if (scale != config.ChatRoomFontScale)
        {
            config.ChatRoomFontScale = scale;
            config.Save();
        }

        ImGui.SameLine();

        var sendWidth = 60f * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - sendWidth - ImGui.GetStyle().ItemSpacing.X);

        if (focusInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusInput = false;
        }

        var submitted = ImGui.InputTextWithHint("##chatinput", "Type a message…",
            ref inputBuf, 2000, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        var clicked = ImGui.Button("Send##chat", new Vector2(sendWidth, 0));

        if (submitted || clicked)
        {
            var text = inputBuf.Trim();
            if (text.Length > 0)
                chat.SendMessage(text);
            inputBuf = string.Empty;
            focusInput = submitted; // keep typing after Enter, release focus after a click
        }
    }

    // ── Direct-message pane ───────────────────────────────────────────────────

    private void OpenDm(string peer)
    {
        if (!openDmPeers.Contains(peer, StringComparer.OrdinalIgnoreCase))
            openDmPeers.Add(peer);
        pendingDmFocus   = peer;
        focusDmInputPeer = peer;
    }

    private void DrawDmPane(string peer)
    {
        var avail       = ImGui.GetContentRegionAvail();
        var inputHeight = ImGui.GetFrameHeightWithSpacing();
        var online      = IsPeerOnline(peer);
        var notice      = chat.LastDmNotice;

        var noticeHeight = 0f;
        if (!online)        noticeHeight += ImGui.GetTextLineHeightWithSpacing();
        if (notice != null) noticeHeight += ImGui.GetTextLineHeightWithSpacing();

        using (var hist = ImRaii.Child($"##dmhist_{peer}", new Vector2(0, avail.Y - inputHeight - noticeHeight), true))
        {
            if (hist) DrawDmHistory(peer);
        }

        if (!online)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.TextUnformatted($"{peer} is offline — direct messages need both users online.");
        }

        if (notice != null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ErrorColor))
                ImGui.TextUnformatted(notice);
        }

        DrawDmInputRow(peer, online);
    }

    private void DrawDmHistory(string peer)
    {
        ImGui.SetWindowFontScale(config.ChatDmFontScale);

        var msgs = chat.SnapshotDm(peer);

        if (msgs.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                ImGui.TextWrapped($"This is the start of your direct messages with {peer}. " +
                                  "Only the two of you can see this conversation.");
            return;
        }

        foreach (var m in msgs)
            DrawMessageLine(m);

        // Keep pinned to the bottom when new messages arrive.
        var last = dmLastCounts.GetValueOrDefault(peer);
        if (msgs.Count != last)
        {
            dmLastCounts[peer] = msgs.Count;
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - ImGui.GetTextLineHeight())
                ImGui.SetScrollHereY(1f);
        }
    }

    private void DrawDmInputRow(string peer, bool online)
    {
        // One shared size for all DM tabs; kept outside the offline-disabled scope so
        // the history stays resizable even when the peer is offline.
        var scale = DrawFontScaleCombo($"##dmFontScale_{peer}", config.ChatDmFontScale);
        if (scale != config.ChatDmFontScale)
        {
            config.ChatDmFontScale = scale;
            config.Save();
        }

        ImGui.SameLine();

        var sendWidth = 60f * ImGuiHelpers.GlobalScale;
        var buf = dmInputBufs.GetValueOrDefault(peer, string.Empty);

        using (ImRaii.Disabled(!online))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - sendWidth - ImGui.GetStyle().ItemSpacing.X);

            if (string.Equals(focusDmInputPeer, peer, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.SetKeyboardFocusHere();
                focusDmInputPeer = null;
            }

            var submitted = ImGui.InputTextWithHint($"##dminput_{peer}", $"Message {peer}…",
                ref buf, 2000, ImGuiInputTextFlags.EnterReturnsTrue);

            ImGui.SameLine();
            var clicked = ImGui.Button($"Send###dmsend_{peer}", new Vector2(sendWidth, 0));

            if (submitted || clicked)
            {
                var text = buf.Trim();
                if (text.Length > 0)
                    chat.SendDirectMessage(peer, text);
                buf = string.Empty;
                if (submitted)
                    focusDmInputPeer = peer; // keep typing after Enter
            }
        }

        dmInputBufs[peer] = buf;
    }

    private bool IsPeerOnline(string peer)
        => chat.SnapshotPresence().Any(u => string.Equals(u.Username, peer, StringComparison.OrdinalIgnoreCase));

    // ── Shared message line ───────────────────────────────────────────────────

    private void DrawMessageLine(ChatMessage m)
    {
        if (m.IsSystem)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, SystemColor))
                ImGui.TextWrapped($"— {m.Text} —");
            return;
        }

        var time = m.Ts > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(m.Ts).LocalDateTime.ToString("HH:mm")
            : string.Empty;

        using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
            ImGui.TextUnformatted($"[{time}]");
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, m.IsFCMember ? FcColor : FriendColor))
            ImGui.TextUnformatted($"{m.Username}:");
        ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
        ImGui.TextWrapped(m.Text);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Compact percent selector for message text size. Returns the (possibly changed) scale.
    /// </summary>
    private static float DrawFontScaleCombo(string id, float current)
    {
        ImGui.SetNextItemWidth(62f * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo(id, $"{(int)MathF.Round(current * 100)}%"))
        {
            if (combo)
            {
                foreach (var step in FontScaleSteps)
                {
                    var selected = Math.Abs(step - current) < 0.005f;
                    if (ImGui.Selectable($"{(int)MathF.Round(step * 100)}%", selected))
                        current = step;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Message text size");
        return current;
    }

    /// <summary>The local character's name, or null when nobody is logged in.</summary>
    private static string? LocalCharacterName()
    {
        var name = (Plugin.ObjectTable.LocalPlayer as ICharacter)?.Name.ToString();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>Positions the cursor so a right-aligned button of the given label fits the line.</summary>
    private static void RightAlignButton(string label)
    {
        var width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
        var target = ImGui.GetContentRegionMax().X - width;
        if (target > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(target);
    }
}
