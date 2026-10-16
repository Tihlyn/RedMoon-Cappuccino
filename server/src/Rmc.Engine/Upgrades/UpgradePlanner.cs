using Rmc.Engine.Damage;
using Rmc.Engine.Melds;
using Rmc.GameData;

namespace Rmc.Engine.Upgrades;

public sealed class UpgradeOptions
{
    /// <summary>Limited currencies earned per week, by name (overlays/weekly.json).</summary>
    public Dictionary<string, int> WeeklyIncome { get; set; } = new(StringComparer.Ordinal);
    /// <summary>What the player holds now, by currency name.</summary>
    public Dictionary<string, int> Wallet { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Consider the best crafted piece as a stopgap for slots still far from their target.</summary>
    public bool IncludeCrafted { get; set; }
    /// <summary>How far ahead the timeline runs.</summary>
    public int MaxWeeks { get; set; } = 26;
    /// <summary>A full re-meld is suggested after a step when it gains at least this fraction.</summary>
    public double RemeldThreshold { get; set; } = 0.0005;
    /// <summary>
    /// Late in a tier, once weekly loot restrictions are lifted: everything counts as affordable now,
    /// and the steps come in order of DPS gain, biggest first.
    /// </summary>
    public bool IgnoreWeeklyLimits { get; set; }
    /// <summary>
    /// Buy augment materials with the hunt currency where an exchange allows it (Thundersteeped Twine
    /// for Sacks of Nuts), keeping the books for gear. Hunt currency isn't weekly-limited.
    /// </summary>
    public bool MaterialsFromHunts { get; set; }
    /// <summary>The hunt currency's name (overlays/weekly.json).</summary>
    public string HuntCurrency { get; set; } = "Sack of Nuts";
    /// <summary>Threads this plan may use (-1: all cores); see <see cref="MeldSolveOptions.MaxDegreeOfParallelism"/>.</summary>
    public int MaxDegreeOfParallelism { get; set; } = -1;
}

public enum StepKind
{
    /// <summary>Bought with limited currency (tomestones, books).</summary>
    Buy,
    /// <summary>Augmenting a piece with a material bought with limited currency.</summary>
    Augment,
    /// <summary>A crafted stopgap (gil, no weekly limit).</summary>
    Craft,
}

public sealed class UpgradeStep
{
    /// <summary>0 = with what the player holds now; 1 = after the first week's income; ...</summary>
    public int Week { get; set; }
    public string Slot { get; set; } = string.Empty;
    public StepKind Kind { get; set; }
    public int FromItemId { get; set; }
    public string FromItem { get; set; } = string.Empty;
    public int ToItemId { get; set; }
    public string ToItem { get; set; } = string.Empty;
    public int ToItemLevel { get; set; }
    /// <summary>Limited currency spent, by name.</summary>
    public Dictionary<string, int> Costs { get; set; } = new(StringComparer.Ordinal);
    /// <summary>What the cost is paid with, readable (e.g. "4× AAC Illustrated: HW Edition III → Thundersteeped Twine").</summary>
    public string CostText { get; set; } = string.Empty;
    public double DpsBefore { get; set; }
    public double DpsAfter { get; set; }
    public double Gain => DpsAfter - DpsBefore;
    /// <summary>Melds for the new piece.</summary>
    public List<MeldInput> Melds { get; set; } = [];
    /// <summary>Other slots whose melds should change after this step (a full re-meld gains enough).</summary>
    public List<string> RemeldSlots { get; set; } = [];
    public double RemeldGain { get; set; }
}

/// <summary>A target piece that only drops (normal raid, alliance raid, ultimate, relic grind): no timeline.</summary>
public sealed class DropUpgrade
{
    public string Slot { get; set; } = string.Empty;
    public int ItemId { get; set; }
    public string Item { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string? SourceDetail { get; set; }
    /// <summary>DPS gain if it dropped now, on top of the plan's final set.</summary>
    public double GainIfObtained { get; set; }
}

public sealed class UpgradePlan
{
    public double CurrentDps { get; set; }
    /// <summary>The current gear with its melds solved.</summary>
    public double CurrentRemeldedDps { get; set; }
    /// <summary>The target set (its sheet's melds) under our model.</summary>
    public double BisDps { get; set; }
    public double FinalDps { get; set; }
    /// <summary>A last full re-meld once every step is done (slots that change, and what it gains).</summary>
    public List<string> FinalRemeldSlots { get; set; } = [];
    public double FinalRemeldGain { get; set; }
    public List<UpgradeStep> Steps { get; set; } = [];
    public List<DropUpgrade> Drops { get; set; } = [];
    /// <summary>The week the last timeline step lands; null when the horizon ran out first.</summary>
    public int? WeeksToFinish { get; set; }
    /// <summary>Timeline steps the horizon didn't reach.</summary>
    public List<string> Unreached { get; set; } = [];
    /// <summary>Target pieces that wouldn't raise DPS over what the plan ends with (e.g. stats this job doesn't use).</summary>
    public List<string> NoGain { get; set; } = [];
    public GearsetInput FinalGear { get; set; } = new();
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Upgrade path from the current gear to a target set, scored by DPS. Each slot is a chain of
/// steps toward its target piece (buy the base piece, then augment it; or buy it outright;
/// optionally a crafted stopgap first). A week-by-week simulation spends the player's wallet and
/// weekly income on the affordable step with the best DPS gain per week of currency, saving a
/// currency instead when a better-valued step that needs it isn't affordable yet. Each new piece
/// is melded on its own, and a full re-meld is suggested when it pays. Pieces that only drop are
/// listed separately with what they'd gain.
/// </summary>
public sealed class UpgradePlanner(StatEngine engine, FittedProfileModel model)
{
    private readonly MeldSolver solver = new(engine, model);

    private sealed class Candidate
    {
        public required string Slot { get; init; }
        public required StepKind Kind { get; init; }
        public required GearItem Item { get; init; }
        public Dictionary<string, int>? RelicStats { get; init; }
        public required Dictionary<string, int> Costs { get; init; }
        public required string CostText { get; init; }
        /// <summary>The chain's next step after this one, if any.</summary>
        public Candidate? Next { get; set; }
        /// <summary>A stopgap: skipped once the chain's real step is taken.</summary>
        public bool Optional { get; init; }
        public bool Done { get; set; }
    }

    public UpgradePlan Plan(GearsetInput current, GearsetInput target, UpgradeOptions options)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var data = engine.Data;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };
        var full_ = new MeldSolveOptions { Fast = true, MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };
        long equipCalls = 0, equipTicks = 0, fullCalls = 0, fullTicks = 0;
        MeldSolution Polish(GearsetInput g)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return solver.Polish(g, new MeldSolveOptions { Fast = true, MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }); }
            finally { fullCalls++; fullTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }
        MeldSolution FullRemeld(GearsetInput g)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return solver.Solve(g, full_); }
            finally { fullCalls++; fullTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }
        var medicine = (Sim.Rotations.RotationRegistry.For(current.Job)?.UsesPotion ?? false) ? engine.BestMedicine(current.Job) : null;
        double Dps(GearsetInput g)
        {
            var st = engine.Compute(g);
            return model.Evaluate(st, medicine != null ? engine.WithMedicine(st, medicine) : null).Dps;
        }

        var state = Clone(current);
        AlignRings(state, target);
        var plan = new UpgradePlan
        {
            CurrentDps = Dps(current),
            CurrentRemeldedDps = FullRemeld(state).Dps,
            BisDps = Dps(target),
        };

        // Chains of steps per slot.
        var chains = new List<Candidate>();
        foreach (var (slot, goal) in target.Slots)
        {
            var have = state.Slots.GetValueOrDefault(slot);
            if (have?.ItemId == goal.ItemId || data.Item(goal.ItemId) is not { } goalItem)
                continue;
            var steps = Route(slot, have?.ItemId, goalItem, goal.RelicStats, options, plan);
            if (steps.Count == 0)
                continue;
            for (var i = 0; i + 1 < steps.Count; i++)
                steps[i].Next = steps[i + 1];
            if (options.IncludeCrafted && BestCrafted(slot, current.Job, have?.ItemId, goalItem) is { } crafted)
                chains.Add(new Candidate
                {
                    Slot = slot, Kind = StepKind.Craft, Item = crafted, Costs = [], CostText = "Crafted (market board or craft)", Optional = true,
                });
            chains.Add(steps[0]);
        }

        // Week by week: spend on the best gain per week of currency while anything is affordable.
        var wallet = new Dictionary<string, int>(options.Wallet, StringComparer.Ordinal);
        var available = new List<Candidate>(chains);
        var stateDps = Dps(state);
        Candidate? forced = null;
        for (var week = 0; week <= options.MaxWeeks && available.Count > 0; week++)
        {
            if (week > 0)
                foreach (var (name, amount) in options.WeeklyIncome)
                    wallet[name] = wallet.GetValueOrDefault(name) + amount;

            while (true)
            {
                // Value every open step, affordable or not: gain per week of currency.
                var valued = new List<(Candidate C, double Ratio, double Dps, GearsetInput State, bool Affordable)>();
                // Each open step is valued on its own: in parallel, one thread per step (their meld
                // solves are a single piece each), collected in the candidates' order.
                var open = forced != null ? [forced] : available.ToList();
                var slotsValued = new (Candidate C, double Ratio, double Dps, GearsetInput State, bool Affordable)?[open.Count];
                var walletNow = new Dictionary<string, int>(wallet, StringComparer.Ordinal);
                Parallel.For(0, open.Count, parallel, k =>
                {
                    var c = open[k];
                    var (trial, dps) = Equip(state, c);
                    var gain = dps - stateDps;
                    if (c.Optional && gain <= 1e-9)
                        return;
                    // A step that's only a means to its chain's end (a base piece to augment) is
                    // valued with the rest of its chain.
                    var chainGain = gain;
                    var chainWeeks = Weeks(c.Costs, options);
                    var at = trial;
                    for (var n = c.Next; n != null && chainGain <= 1e-9; n = n.Next)
                    {
                        (at, var chainDps) = Equip(at, n);
                        chainGain = chainDps - stateDps;
                        chainWeeks += Weeks(n.Costs, options);
                    }
                    if (chainGain <= 1e-9 && c.Next == null)
                        return;
                    // A step that loses damage on its own waits until the rest of its chain can follow at once.
                    var affordable = options.IgnoreWeeklyLimits || c == forced
                        || Affordable(c, walletNow, options) && (gain > 1e-9 || ChainAffordable(c, walletNow, options));
                    // Without weekly limits the order is simply the biggest gain first.
                    var ratio = options.IgnoreWeeklyLimits ? chainGain : chainGain / Math.Max(chainWeeks, 0.01);
                    slotsValued[k] = (c, ratio, dps, trial, affordable);
                });
                foreach (var v in slotsValued)
                    if (v is { } value)
                        valued.Add(value);

                // Take the best affordable step, unless a better one shares its currency: then save
                // that currency for it instead of spending it on something worth less per week.
                var reserved = new HashSet<string>(StringComparer.Ordinal);
                (Candidate C, double Ratio, double Dps, GearsetInput State, bool Affordable)? pick = null;
                foreach (var v in valued.OrderByDescending(v => v.Ratio))
                {
                    if (v.Affordable && !v.C.Costs.Keys.Any(reserved.Contains))
                    {
                        pick = v;
                        break;
                    }
                    if (!v.Affordable)
                        reserved.UnionWith(v.C.Costs.Keys);
                }
                if (pick is not { } chosen)
                {
                    forced = null;
                    break;
                }
                var best = chosen.C;
                var bestDps = chosen.Dps;
                var bestState = chosen.State;
                // A step that loses damage was only affordable with the rest of its chain: take that next.
                forced = bestDps < stateDps - 1e-9 && best.Next != null ? best.Next : null;

                foreach (var (name, amount) in best.Costs)
                    wallet[name] = wallet.GetValueOrDefault(name) - amount;
                available.Remove(best);
                if (best.Next != null)
                    available.Add(best.Next);
                if (!best.Optional)
                    available.RemoveAll(c => c.Optional && c.Slot == best.Slot);

                var step = new UpgradeStep
                {
                    Week = week,
                    Slot = best.Slot,
                    Kind = best.Kind,
                    FromItemId = state.Slots.GetValueOrDefault(best.Slot)?.ItemId ?? 0,
                    FromItem = data.Item(state.Slots.GetValueOrDefault(best.Slot)?.ItemId ?? 0)?.Name ?? "(empty)",
                    ToItemId = best.Item.Id,
                    ToItem = best.Item.Name,
                    ToItemLevel = best.Item.ItemLevel,
                    Costs = best.Costs,
                    CostText = best.CostText,
                    DpsBefore = stateDps,
                    DpsAfter = bestDps,
                    Melds = bestState.Slots[best.Slot].Materia.Select(m => new MeldInput { Stat = m.Stat, Grade = m.Grade }).ToList(),
                };

                // Would re-melding everything gain enough to suggest it?
                // A polish from the current melds is enough to tell whether re-melding pays.
                var full = Polish(bestState);
                if (full.Dps > bestDps * (1 + options.RemeldThreshold))
                {
                    step.RemeldGain = full.Dps - bestDps;
                    step.RemeldSlots = full.Gear.Slots
                        .Where(kv => kv.Key != best.Slot && !SameMelds(kv.Value.Materia, bestState.Slots[kv.Key].Materia))
                        .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
                    step.Melds = full.Gear.Slots[best.Slot].Materia;
                    bestState = full.Gear;
                    bestDps = full.Dps;
                }
                state = bestState;
                stateDps = bestDps;
                plan.Steps.Add(step);
            }
        }

        var reached = plan.Steps.Count > 0 ? plan.Steps.Max(s => s.Week) : 0;
        foreach (var c in available.Where(c => !c.Optional))
        {
            // Would the rest of this chain raise DPS from where the plan ends?
            var (at, dps) = Equip(state, c);
            for (var n = c.Next; n != null; n = n.Next)
                (at, dps) = Equip(at, n);
            (dps > stateDps + 1e-9 ? plan.Unreached : plan.NoGain).Add($"{c.Slot}: {c.Item.Name} ({c.CostText})");
        }
        plan.WeeksToFinish = plan.Unreached.Count == 0 ? reached : null;

        // What each drop-only piece would add on top of the final set.
        foreach (var drop in plan.Drops)
        {
            var goal = target.Slots[drop.Slot];
            var c = new Candidate
            {
                Slot = drop.Slot, Kind = StepKind.Buy, Item = data.Item(drop.ItemId)!, RelicStats = goal.RelicStats, Costs = [], CostText = "",
            };
            drop.GainIfObtained = Equip(state, c).Dps - stateDps;
        }

        // End on the final gear fully re-melded: earlier steps only re-meld when it pays at the time.
        // Exact, once: the plan's end shouldn't depend on the path's local optimum.
        var final = solver.Solve(state, new MeldSolveOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism });
        if (final.Dps > stateDps + 1e-9)
        {
            plan.FinalRemeldGain = final.Dps - stateDps;
            plan.FinalRemeldSlots = final.Gear.Slots.Where(kv => !SameMelds(kv.Value.Materia, state.Slots[kv.Key].Materia))
                .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            state = final.Gear;
            stateDps = final.Dps;
        }
        plan.FinalDps = stateDps;
        plan.FinalGear = state;
        if (Environment.GetEnvironmentVariable("RMC_SOLVER_TRACE") != null)
            Console.Error.WriteLine($"  planner: {equipCalls} piece solves {equipTicks / (double)System.Diagnostics.Stopwatch.Frequency:0.00}s (summed over threads), "
                + $"{fullCalls} full re-melds {fullTicks / (double)System.Diagnostics.Stopwatch.Frequency:0.00}s, total {watch.Elapsed.TotalSeconds:0.00}s");
        plan.Elapsed = watch.Elapsed;
        return plan;

        (GearsetInput State, double Dps) Equip(GearsetInput from, Candidate c)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return EquipCore(from, c); }
            finally { Interlocked.Increment(ref equipCalls); Interlocked.Add(ref equipTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
        }
        (GearsetInput State, double Dps) EquipCore(GearsetInput from, Candidate c)
        {
            var next = Clone(from);
            next.Slots[c.Slot] = new PieceInput { ItemId = c.Item.Id, RelicStats = c.RelicStats };
            // Meld only the new piece; everything else keeps its melds.
            var locked = next.Slots.Keys.Where(k => k != c.Slot).ToHashSet(StringComparer.Ordinal);
            var solved = solver.Solve(next, new MeldSolveOptions { LockedSlots = locked, MaxDegreeOfParallelism = 1 });
            // Some pieces only pay once the rest is re-melded around them: before calling one a loss,
            // polish the whole set.
            if (solved.Dps <= Dps(from) + 1e-9)
            {
                var polished = solver.Polish(solved.Gear, new MeldSolveOptions { Fast = true, MaxDegreeOfParallelism = 1 });
                if (polished.Dps > solved.Dps)
                    return (polished.Gear, polished.Dps);
            }
            return (solved.Gear, solved.Dps);
        }
    }

    /// <summary>The steps from the piece in a slot to its target, by the target's source.</summary>
    private List<Candidate> Route(string slot, int? haveId, GearItem goal, Dictionary<string, int>? relicStats, UpgradeOptions options, UpgradePlan plan)
    {
        var data = engine.Data;
        var steps = new List<Candidate>();
        if (goal.Source.Augment is { } augment && data.Item(augment.BaseItemId) is { } baseItem && goal.Source.Type is SourceTypes.Tome or SourceTypes.Savage)
        {
            if (haveId != baseItem.Id)
            {
                if (Purchase(slot, baseItem, null, StepKind.Buy, baseItem.Source.Costs ?? [], options) is not { } buyBase)
                    return Drop(slot, goal, plan);
                steps.Add(buyBase);
            }
            if (Purchase(slot, goal, relicStats, StepKind.Augment, augment.Materials, options) is not { } doAugment)
                return Drop(slot, goal, plan);
            steps.Add(doAugment);
            return steps;
        }
        if (Purchase(slot, goal, relicStats, StepKind.Buy, goal.Source.Costs ?? [], options) is { } buy && buy.Costs.Count > 0)
            return [buy];
        return Drop(slot, goal, plan);
    }

    private List<Candidate> Drop(string slot, GearItem goal, UpgradePlan plan)
    {
        plan.Drops.Add(new DropUpgrade
        {
            Slot = slot, ItemId = goal.Id, Item = goal.Name, SourceType = goal.Source.Type, SourceDetail = goal.Source.Detail,
        });
        return [];
    }

    /// <summary>
    /// A step paid in limited currency: each cost is income-backed itself, or a material whose
    /// cheapest income-backed exchange is used (Twine for four books III). Null when something
    /// in it can only drop.
    /// </summary>
    private Candidate? Purchase(string slot, GearItem item, Dictionary<string, int>? relicStats, StepKind kind, List<CurrencyCost> costs, UpgradeOptions options)
    {
        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        var text = new List<string>();
        foreach (var cost in costs)
        {
            if (options.WeeklyIncome.ContainsKey(cost.Name))
            {
                total[cost.Name] = total.GetValueOrDefault(cost.Name) + cost.Amount;
                text.Add($"{cost.Amount:N0}× {cost.Name}");
                continue;
            }
            var exchanges = engine.Data.Snapshot.Currencies.FirstOrDefault(c => c.Name == cost.Name)?.Exchanges ?? [];
            var exchange = (options.MaterialsFromHunts ? exchanges.FirstOrDefault(e => e.All(x => x.Name == options.HuntCurrency)) : null)
                ?? exchanges.Where(e => e.All(x => options.WeeklyIncome.ContainsKey(x.Name)))
                    .MinBy(e => e.Sum(x => (double)x.Amount / options.WeeklyIncome[x.Name]));
            if (exchange == null)
                return null;
            foreach (var x in exchange)
                total[x.Name] = total.GetValueOrDefault(x.Name) + x.Amount * cost.Amount;
            text.Add($"{cost.Amount}× {cost.Name} ({string.Join(" + ", exchange.Select(x => $"{x.Amount * cost.Amount}× {x.Name}"))})");
        }
        return new Candidate { Slot = slot, Kind = kind, Item = item, RelicStats = relicStats, Costs = total, CostText = string.Join(" + ", text) };
    }

    /// <summary>The best crafted piece for a slot (highest item level) that the job can wear, if it beats what's there.</summary>
    private GearItem? BestCrafted(string slot, string job, int? haveId, GearItem goal)
    {
        var data = engine.Data;
        var have = haveId is int id ? data.Item(id) : null;
        var shape = goal.Slot;
        return data.Snapshot.Items
            .Where(i => i.Crafted && i.Slot == shape && i.Jobs.Contains(job) && i.ItemLevel > (have?.ItemLevel ?? 0) && i.ItemLevel < goal.ItemLevel)
            .MaxBy(i => (i.ItemLevel, i.AdvancedMelding));
    }

    /// <summary>The step and every step after it in its chain, paid together.</summary>
    private static bool ChainAffordable(Candidate c, Dictionary<string, int> wallet, UpgradeOptions options)
    {
        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var n = c; n != null; n = n.Next)
            foreach (var (name, amount) in n.Costs)
                total[name] = total.GetValueOrDefault(name) + amount;
        return total.All(kv => !options.WeeklyIncome.ContainsKey(kv.Key) || wallet.GetValueOrDefault(kv.Key) >= kv.Value);
    }

    /// <summary>Weekly-limited costs must be covered; anything else (hunt currency) is taken as available.</summary>
    private static bool Affordable(Candidate c, Dictionary<string, int> wallet, UpgradeOptions options)
        => c.Costs.All(kv => !options.WeeklyIncome.ContainsKey(kv.Key) || wallet.GetValueOrDefault(kv.Key) >= kv.Value);

    /// <summary>Cost in weeks of income: the binding currency's weeks.</summary>
    private static double Weeks(Dictionary<string, int> costs, UpgradeOptions options)
        => costs.Where(kv => options.WeeklyIncome.ContainsKey(kv.Key)).Select(kv => (double)kv.Value / Math.Max(1, options.WeeklyIncome[kv.Key])).DefaultIfEmpty(0).Max();

    private static bool SameMelds(List<MeldInput> a, List<MeldInput> b)
        => a.Count == b.Count && a.Zip(b).All(x => x.First.Stat == x.Second.Stat && x.First.Grade == x.Second.Grade);

    /// <summary>Rings are interchangeable: put each current ring where the target wants that item.</summary>
    private static void AlignRings(GearsetInput state, GearsetInput target)
    {
        if (!state.Slots.TryGetValue(Slots.RingLeft, out var left) || !state.Slots.TryGetValue(Slots.RingRight, out var right))
            return;
        int Matches(PieceInput l, PieceInput r)
            => (target.Slots.GetValueOrDefault(Slots.RingLeft)?.ItemId == l.ItemId ? 1 : 0) + (target.Slots.GetValueOrDefault(Slots.RingRight)?.ItemId == r.ItemId ? 1 : 0);
        if (Matches(right, left) > Matches(left, right))
            (state.Slots[Slots.RingLeft], state.Slots[Slots.RingRight]) = (right, left);
    }

    private static GearsetInput Clone(GearsetInput input) => new()
    {
        Job = input.Job, Level = input.Level, ClanId = input.ClanId, PartyBonus = input.PartyBonus, Food = input.Food,
        Slots = input.Slots.ToDictionary(kv => kv.Key, kv => new PieceInput
        {
            ItemId = kv.Value.ItemId, Hq = kv.Value.Hq, RelicStats = kv.Value.RelicStats,
            Materia = kv.Value.Materia.Select(m => new MeldInput { Stat = m.Stat, Grade = m.Grade }).ToList(),
        }, StringComparer.Ordinal),
    };
}
