using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RedMoonCappuccino.Models;
using RedMoonCappuccino.Services;

namespace RedMoonCappuccino.Windows;

/// <summary>
/// Standalone live-chat window. Opened via the <c>/livechat</c> command. Defaults to a
/// logged-off state; the user logs in/off against the chat WS with the header button.
/// Members list on the left, chat history + input on the right.
/// </summary>
public sealed class ChatWindow : Window, IDisposable
{
    private readonly ChatService chat;

    private string usernameBuf = string.Empty;
    private string inputBuf = string.Empty;
    private bool prefilled;
    private int lastMessageCount;
    private bool focusInput;

    // FC member = gold; FC friend (non-FC) = muted blue-grey.
    private static readonly Vector4 FcColor     = new(0.95f, 0.82f, 0.36f, 1f);
    private static readonly Vector4 FriendColor = new(0.62f, 0.74f, 0.88f, 1f);
    private static readonly Vector4 MutedColor  = new(0.55f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 SystemColor = new(0.50f, 0.62f, 0.50f, 1f);
    private static readonly Vector4 ErrorColor  = new(0.90f, 0.35f, 0.35f, 1f);

    public ChatWindow(ChatService chat)
        : base("Live Chat##RmcChatWindow")
    {
        this.chat = chat;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 320),
            MaximumSize = new Vector2(1200, 1000),
        };
        Size          = new Vector2(620, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        PrefillUsername();
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
            if (pane) DrawChatPane();
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
                using (ImRaii.PushColor(ImGuiCol.Text, chat.IsFCMember ? FcColor : FriendColor))
                    ImGui.TextUnformatted($"●  {chat.ResolvedUsername}  [{tag}]");
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
                using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
                    ImGui.TextUnformatted("Offline");
                ImGui.SameLine();

                ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
                var enter = ImGui.InputTextWithHint("##chatuser", "Character name",
                    ref usernameBuf, 32, ImGuiInputTextFlags.EnterReturnsTrue);

                ImGui.SameLine();
                if (ImGui.Button("Log In##chat") || enter)
                    chat.Connect(usernameBuf);
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
                "You are logged off. Enter your FFXIV character name above and press " +
                "\"Log In\" to join the live chat.");
            ImGui.Spacing();
            ImGui.TextWrapped(
                "Your name is resolved against the FC roster — both FC members and FC " +
                "friends can join.");
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
            var isMe = string.Equals(u.Username, chat.ResolvedUsername, StringComparison.Ordinal);
            using (ImRaii.PushColor(ImGuiCol.Text, u.IsFCMember ? FcColor : FriendColor))
                ImGui.TextUnformatted(isMe ? $"● {u.Username} (you)" : $"● {u.Username}");
        }
    }

    // ── Chat pane ─────────────────────────────────────────────────────────────

    private void DrawChatPane()
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
        var msgs = chat.SnapshotMessages();

        foreach (var m in msgs)
        {
            if (m.IsSystem)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, SystemColor))
                    ImGui.TextWrapped($"— {m.Text} —");
                continue;
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>On first open, seed the username field with the local character's name.</summary>
    private void PrefillUsername()
    {
        if (prefilled || !string.IsNullOrEmpty(usernameBuf)) return;
        var name = (Plugin.ObjectTable.LocalPlayer as ICharacter)?.Name.ToString();
        if (!string.IsNullOrEmpty(name))
        {
            usernameBuf = name!;
            prefilled = true;
        }
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
