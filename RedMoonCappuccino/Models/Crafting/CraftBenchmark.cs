namespace RedMoonCappuccino.Models.Crafting;

/// <summary>
/// The character and recipe every solver measurement is taken against.
///
/// <para>Ground truth, in the same sense as the durability and CP pool: a number that is allowed
/// to appear in exactly one place. Stat lines were previously written out at each call site, and
/// the benchmark ended up running on 3350 craftsmanship / 4750 control — values copy-pasted from
/// the replay check, where they are correct because they are solved from a recording made on a
/// weaker alt. Every adaptive-policy figure produced before that was found described the wrong
/// character; the search was read as clearing 0.8% when on this one it clears 32%.</para>
///
/// <para>A replay check should still derive its stats from the recording it replays. Everything
/// else — every Monte Carlo batch, every policy comparison, every tuning run — belongs here.</para>
/// </summary>
public static class CraftBenchmark
{
    /// <summary>
    /// Fully buffed, as the recorded manual expert crafts were played: HQ jhinga biryani and
    /// HQ Cunning Craftsman's Draught, with a relic tool for the 1.75 multiplier on Good.
    /// </summary>
    public static PlayerSpec Character => new()
    {
        // From the character sheet, food and potion included. Only meaningful alongside the
        // ExpertRecipe dividers below — stats alone do not determine what an action is worth, the
        // recipe's dividers do half the work, and separating the two is what inflated this
        // benchmark for the whole of its life. Change them together or not at all.
        //
        // The plugin's own live read comes in about 150 craftsmanship and 30 control under these,
        // which is unexplained and tracked separately; it is roughly 2% and does not change any
        // conclusion drawn here.
        Craftsmanship = 5876,
        Control = 5647,
        MaxCp = 793,
        Level = 100,
        GoodMultiplier = 1.75,

        // Expert recipes are the case delineations exist for, and the advisor is not asked to
        // ration them; treating them as scarce would only bias the search away from the actions
        // the recipe is designed around.
        AvailableDelineations = int.MaxValue,
    };

    /// <summary>
    /// The expert recipe the condition model was fitted against — flag 1523, which telegraphs.
    /// Its requirement is 31,500 of 31,520, so clearing means near-perfect quality.
    /// </summary>
    public static RecipeSpec ExpertRecipe => new()
    {
        RecipeId = 38247,
        ConditionsFlag = 1523,
        IsExpert = true,
        RecipeJobLevel = 100,
        Difficulty = 11250,
        MaxQuality = 31520,
        Durability = 60,
        RequiredQuality = 31500,
        // Read from the client, not assumed. These were written as flat 100s and that inflated the
        // whole benchmark: base progress is craftsmanship x 10 / ProgressDivider + 2, so a divider
        // of 100 against a real 180 made the benchmark character 1.85x stronger than any real one.
        // Every clear rate this project reported before 2026-08-19 was measured against that
        // character. The macro replay in the same test suite had solved the true dividers for
        // another recipe and asserted them — 189 and 207 — and the disagreement sat in one file,
        // unnoticed, because nothing compared the two.
        ProgressDivider = 180,
        QualityDivider = 180,
        ProgressModifier = 100,
        QualityModifier = 100,
    };

    /// <summary>Condition flag the fitted model must cover for a benchmark run to be meaningful.</summary>
    public const int ConditionsFlag = 1523;
}
