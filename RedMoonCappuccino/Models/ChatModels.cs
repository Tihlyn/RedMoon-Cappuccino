using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RedMoonCappuccino.Models;

// Wire models for the live-chat WebSocket (chat-ws/v1).
// See docs/live_chat.md for the full contract.

/// <summary>A single chat message, as stored in history and broadcast per message.</summary>
public class ChatMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("isFCMember")]
    public bool IsFCMember { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Epoch milliseconds.</summary>
    [JsonPropertyName("ts")]
    public long Ts { get; set; }

    /// <summary>UI-only marker for locally-synthesised room notices (join/leave).</summary>
    [JsonIgnore]
    public bool IsSystem { get; set; }
}

/// <summary>An online member entry in a presence frame.</summary>
public class ChatPresenceUser
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("isFCMember")]
    public bool IsFCMember { get; set; }
}

/// <summary>hello — sent once on connect.</summary>
public class ChatHelloMessage : WsMessage
{
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = string.Empty;
}

/// <summary>joined — acknowledges a successful join with the resolved identity.</summary>
public class ChatJoinedMessage : WsMessage
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("isFCMember")]
    public bool IsFCMember { get; set; }
}

/// <summary>history — recent messages in chronological order.</summary>
public class ChatHistoryMessage : WsMessage
{
    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();
}

/// <summary>presence — the current set of online users.</summary>
public class ChatPresenceMessage : WsMessage
{
    [JsonPropertyName("users")]
    public List<ChatPresenceUser> Users { get; set; } = new();

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>message — a single broadcast chat message wrapped in an envelope.</summary>
public class ChatMessageEnvelope : WsMessage
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }
}

/// <summary>system — lightweight room notice (user_joined / user_left).</summary>
public class ChatSystemMessage : WsMessage
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("ts")]
    public long Ts { get; set; }
}

/// <summary>error — any rejected request.</summary>
public class ChatErrorMessage : WsMessage
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("requestType")]
    public string? RequestType { get; set; }
}
