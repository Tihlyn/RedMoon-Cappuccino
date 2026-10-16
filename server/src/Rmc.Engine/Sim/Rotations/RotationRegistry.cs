namespace Rmc.Engine.Sim.Rotations;

/// <summary>The rotation sim available for each job (jobs without one fall back to profiles).</summary>
public static class RotationRegistry
{
    /// <summary>Where refresh-potencies writes the tooltip potencies, relative to server/.</summary>
    public static readonly string DefaultPotencyDirectory = Path.Combine("data", "potencies");

    private static readonly Dictionary<string, Func<Rotation>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AST"] = () => new AstRotation(),
        ["BLM"] = () => new BlmRotation(),
        ["BRD"] = () => new BrdRotation(),
        ["DNC"] = () => new DncRotation(),
        ["DRG"] = () => new DrgRotation(),
        ["DRK"] = () => new DrkRotation(),
        ["GNB"] = () => new GnbRotation(),
        ["MCH"] = () => new MchRotation(),
        ["MNK"] = () => new MnkRotation(),
        ["NIN"] = () => new NinRotation(),
        ["PCT"] = () => new PctRotation(),
        ["PLD"] = () => new PldRotation(),
        ["RDM"] = () => new RdmRotation(),
        ["RPR"] = () => new RprRotation(),
        ["SAM"] = () => new SamRotation(),
        ["SCH"] = () => new SchRotation(),
        ["SGE"] = () => new SgeRotation(),
        ["SMN"] = () => new SmnRotation(),
        ["VPR"] = () => new VprRotation(),
        ["WAR"] = () => new WarRotation(),
        ["WHM"] = () => new WhmRotation(),
    };

    public static IReadOnlyCollection<string> Jobs => Factories.Keys;

    /// <summary>
    /// A fresh rotation (they carry combo/gauge state) with the job's tooltip potencies applied
    /// when <paramref name="potencyDirectory"/> has a table for it; null when the job has no sim yet.
    /// </summary>
    public static Rotation? For(string job, string? potencyDirectory = null)
    {
        if (!Factories.TryGetValue(job, out var make))
            return null;
        var rotation = make();
        if (potencyDirectory != null)
            PotencyTable.Load(potencyDirectory, job)?.ApplyTo(rotation);
        return rotation;
    }
}
