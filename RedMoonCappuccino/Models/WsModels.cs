using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedMoonCappuccino.Models;

public class WsMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

public class TaxInfo
{
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("rate")]
    public int Rate { get; set; }
}

public class EventParticipant
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("class")]
    public string Class { get; set; } = string.Empty;
}

public class EventSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTime Date { get; set; }

    [JsonPropertyName("organizer")]
    public string Organizer { get; set; } = string.Empty;

    [JsonPropertyName("organizerName")]
    public string OrganizerName { get; set; } = string.Empty;

    [JsonPropertyName("participants")]
    public List<EventParticipant> Participants { get; set; } = new();

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("groupType")]
    public string GroupType { get; set; } = string.Empty;
}

public class ImageManifest
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

public class SnapshotMessage : WsMessage
{
    [JsonPropertyName("tax")]
    public TaxInfo? Tax { get; set; }

    [JsonPropertyName("events")]
    public List<EventSummary> Events { get; set; } = new();

    [JsonPropertyName("images")]
    public List<ImageManifest> Images { get; set; } = new();

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

public class EventResponseMessage : WsMessage
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("event")]
    public EventSummary? Event { get; set; }
}

public class ImageData
{
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;
}

public class ImageResponseMessage : WsMessage
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("image")]
    public ImageData? Image { get; set; }
}

public class AcqSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// Captures any extra properties the server includes per source type.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class AcqResultData
{
    [JsonPropertyName("itemId")]
    public int ItemId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("sources")]
    public List<AcqSource> Sources { get; set; } = new();

    [JsonPropertyName("reducesInto")]
    public JsonElement? ReducesInto { get; set; }
}

public class AcqResultMessage : WsMessage
{
    [JsonPropertyName("data")]
    public AcqResultData? Data { get; set; }
}
