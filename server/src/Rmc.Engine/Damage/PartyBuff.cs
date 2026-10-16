namespace Rmc.Engine.Damage;

/// <summary>A party raid buff (game values; xivgear's buff list).</summary>
public sealed class PartyBuff
{
    public string Name { get; set; } = string.Empty;
    public double DamagePercent { get; set; }
    public double CritRate { get; set; }
    public double DirectHitRate { get; set; }
}
