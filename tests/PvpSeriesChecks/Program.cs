using RedMoonCappuccino.Models;
using RedMoonCappuccino.Services;

namespace Harness;

public static class Program
{
    private static int failures;
    private static int checks;

    /// <summary>
    /// Running total needed to have reached each level, as published by the community
    /// Malmstone calculators (belthesar / Arcfalt) and confirmed against the game's
    /// PvPSeriesLevel sheet. Index = level. The calculator must reproduce this exactly.
    /// </summary>
    private static readonly long[] PublishedTotals =
    {
        0, 0, 2000, 4000, 6000, 8000, 11000, 14000, 17000, 20000, 23000, 27000,
        31000, 35000, 39000, 43000, 48500, 54000, 59500, 65000, 70500, 78000, 85500,
        93000, 100500, 108000, 118000, 128000, 138000, 148000, 158000, 178000, 198000,
        218000, 238000, 258000, 278000, 298000, 318000, 338000, 358000,
    };

    public static int Main()
    {
        var calc = new PvpSeriesCalculator(PvpSeriesCalculator.FallbackExpToNext);

        Section("A. Level curve against the published totals");
        for (var level = 1; level < PublishedTotals.Length; level++)
        {
            var got = calc.TotalExpAtLevel(level);
            Check($"total at level {level} == {PublishedTotals[level]} (got {got})", got == PublishedTotals[level]);
        }
        Check("rank 25 costs 108,000 in total", calc.TotalExpAtLevel(PvpSeriesCalculator.TargetRank) == 108_000);
        Check("levels past the table keep costing 20,000", calc.ExpToNext(45) == 20_000);
        Check("level 0 is treated as level 1", calc.ExpToNext(0) == 2_000 && calc.TotalExpAtLevel(0) == 0);

        Section("B. Matches-needed rounding");
        Check("nothing left needs no matches", PvpSeriesCalculator.MatchesNeeded(0, 900) == 0);
        Check("an exact multiple is not rounded up", PvpSeriesCalculator.MatchesNeeded(1800, 900) == 2);
        Check("one EXP over rounds up", PvpSeriesCalculator.MatchesNeeded(1801, 900) == 3);
        Check("one EXP short still needs the match", PvpSeriesCalculator.MatchesNeeded(1, 900) == 1);

        Section("C. Plan — the same figures the community calculator gives");
        // Level 17 with 2,350 into the level: 54,000 + 2,350 earned, 51,650 to go.
        var mid = new PvpSeriesSnapshot { Status = PvpSeriesStatus.Ok, Rank = 17, Experience = 2350 };
        var plan = calc.Plan(mid);
        Check($"current total 56,350 (got {plan.CurrentTotalExp})", plan.CurrentTotalExp == 56_350);
        Check($"remaining 51,650 (got {plan.RemainingExp})", plan.RemainingExp == 51_650);
        Check($"exp to next level 5,500 (got {plan.ExpToNextLevel})", plan.ExpToNextLevel == 5_500);
        Check("target not reached", !plan.TargetReached);

        var byName = plan.Requirements.ToDictionary(r => r.Mode.Name);
        Check("four modes reported", plan.Requirements.Count == 4);
        Check("CC: 58 wins / 74 losses",
            byName["Crystalline Conflict"] is { First: 58, Second: null, Third: 74 });
        Check("Frontline: 35 / 42 / 52",
            byName["Frontline"] is { First: 35, Second: 42, Third: 52 });
        Check("Frontline daily: 18 / 19 / 21",
            byName["Frontline (daily bonus)"] is { First: 18, Second: 19, Third: 21 });
        Check("Rival Wings: 42 wins / 69 losses",
            byName["Rival Wings"] is { First: 42, Second: null, Third: 69 });

        Section("D. Plan — edge cases");
        var fresh = calc.Plan(new PvpSeriesSnapshot { Status = PvpSeriesStatus.Ok, Rank = 1, Experience = 0 });
        Check("a fresh series needs the full 108,000", fresh.RemainingExp == 108_000);
        Check("fresh series: 120 CC wins", fresh.Requirements[0].First == 120);

        var lastStep = calc.Plan(new PvpSeriesSnapshot { Status = PvpSeriesStatus.Ok, Rank = 24, Experience = 7_499 });
        Check("one EXP short of rank 25 needs one match of anything", lastStep.RemainingExp == 1 &&
            lastStep.Requirements.All(r => r.First == 1 && r.Third == 1 && (r.Second ?? 1) == 1));

        var reached = calc.Plan(new PvpSeriesSnapshot { Status = PvpSeriesStatus.Ok, Rank = 25, Experience = 0 });
        Check("rank 25 exactly is reached with nothing remaining", reached.TargetReached && reached.RemainingExp == 0);
        Check("reached: every requirement is zero", reached.Requirements.All(r => r.First == 0 && r.Third == 0));

        var past = calc.Plan(new PvpSeriesSnapshot { Status = PvpSeriesStatus.Ok, Rank = 33, Experience = 12_000 });
        Check("past the target stays reached, never negative", past.TargetReached && past.RemainingExp == 0);
        Check("past the table: progress bar denominator is 20,000", past.ExpToNextLevel == 20_000);

        Section("E. Game-sheet shaped curve behaves like the fallback");
        // The service hands over the PvPSeriesLevel rows as an array indexed by level (0..30).
        var sheetShaped = Enumerable.Range(0, 31).Select(l => PvpSeriesCalculator.FallbackExpToNext[l]).ToArray();
        var fromSheet = new PvpSeriesCalculator(sheetShaped);
        Check("sheet-shaped curve agrees at rank 25", fromSheet.TotalExpAtLevel(25) == 108_000);
        Check("sheet-shaped curve agrees at rank 40", fromSheet.TotalExpAtLevel(40) == PublishedTotals[40]);

        var tooShort = false;
        try { _ = new PvpSeriesCalculator(new[] { 0 }); } catch (ArgumentException) { tooShort = true; }
        Check("a curve without level 1 is refused", tooShort);

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine(failures == 0
            ? $"ALL {checks} CHECKS PASSED"
            : $"{failures} of {checks} CHECKS FAILED");

        return failures == 0 ? 0 : 1;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine(title);
        Console.WriteLine(new string('=', 72));
    }

    private static void Check(string label, bool ok, string? note = null)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (note != null && !ok) Console.WriteLine($"          {note}");
    }
}
