using System;

namespace RedMoonCappuccino.RotationRecorder;

/// <summary>
/// A single recorded player action. UseAction fires on key press (client-side).
/// DamageDealt / WasCrit / WasDh are populated later if you wire ReceiveActionEffect.
/// </summary>
public sealed class ActionEvent
{
    public DateTimeOffset Timestamp    { get; init; }
    public uint           ActionId     { get; init; }
    public string         ActionName   { get; init; } = "";
    public bool           IsGcd        { get; init; }   // ActionCategory 2=Weaponskill or 3=Spell
    public uint           Mp           { get; init; }
    public uint           MaxMp        { get; init; }
    public bool           WasOnCooldown { get; init; }  // true = pressed while still on cooldown

    // Populated by ReceiveActionEffect (optional second hook)
    public uint? DamageDealt { get; set; }
    public bool? WasCrit     { get; set; }
    public bool? WasDh       { get; set; }
}
