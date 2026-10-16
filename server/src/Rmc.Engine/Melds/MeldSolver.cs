using System.Diagnostics;
using Rmc.Engine.Damage;
using Rmc.GameData;

namespace Rmc.Engine.Melds;

public sealed class MeldSolveOptions
{
    /// <summary>Keep the melds already on each piece and only fill its empty slots.</summary>
    public bool KeepExistingMelds { get; set; }
    /// <summary>Gear slots whose melds stay exactly as they are.</summary>
    public HashSet<string> LockedSlots { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Only sets with this GCD (2.5s base with the job's trait haste, centiseconds).</summary>
    public int? TargetGcdCs { get; set; }
    /// <summary>Healers: only sets with at least this much piety (piety materia become an option).</summary>
    public int? MinPiety { get; set; }
    /// <summary>Also choose the food, among the current tier's foods for the job's stats.</summary>
    public bool SolveFood { get; set; }
    /// <summary>
    /// Coarser pruning for bulk use (ranking many candidate gear sets): between pieces, states
    /// within one grade XI materia (18 points) of another on every stat are dropped. Several times
    /// faster on pentamelded sets; can miss the best melds by a few hundredths of a percent.
    /// </summary>
    public bool Fast { get; set; }
    /// <summary>
    /// Threads this solve may use (-1: all cores). A server running several solves at once gives
    /// each a share of the machine instead of letting them all claim every core.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = -1;
}

public sealed class MeldSolution
{
    /// <summary>The input with the solved melds (and food).</summary>
    public GearsetInput Gear { get; set; } = new();
    public GearsetStats Stats { get; set; } = new();
    public double Dps { get; set; }
    /// <summary>The search's winner as the fast stat path scored it, before the local search.</summary>
    public double SearchDps { get; set; }
    /// <summary>The same winner rescored with a full stat computation; must equal <see cref="SearchDps"/>.</summary>
    public double SearchDpsRecomputed { get; set; }
    /// <summary>DPS of the input as given, under the same model.</summary>
    public double CurrentDps { get; set; }
    public double Gain => Dps - CurrentDps;
    public int GcdCs { get; set; }
    /// <summary>Complete meld combinations scored with the damage model.</summary>
    public int CandidatesScored { get; set; }
    /// <summary>Meld changes (single or paired) the local search kept.</summary>
    public int LocalSearchSwaps { get; set; }
    public TimeSpan Elapsed { get; set; }
    /// <summary>No set satisfied the constraints (target GCD, minimum piety).</summary>
    public bool Infeasible { get; set; }
}

/// <summary>
/// Best materia for a gear set under a damage model.
/// 1. Each piece's free slots are enumerated (normal slots take grade XII; five-slot overmelds
///    grade XII in the first overmeld slot and XI after; overmelds always succeed) and reduced to
///    distinct, non-dominated stat gains.
/// 2. The pieces are combined one at a time (fewest options first), keeping for every exact speed
///    total the Pareto front of the other damage stats. Speed is a key rather than a dimension
///    because more speed isn't always more damage. The front is exact unless Fast is set.
/// 3. The last piece goes straight into tier vectors: damage only sees each substat through its
///    tier (floor functions), so combinations whose final stats (food included) share every tier
///    are the same set to the model, and a tier vector beaten on every stat at the same speed tier
///    can't win. The rest are scored exactly with the fitted profile.
/// 4. A local search on the winner (single-meld and two-meld changes, full stat computation) polishes it.
/// </summary>
public sealed class MeldSolver(StatEngine engine, FittedProfileModel model)
{
    private const int FullGrade = 12;
    private const int OvermeldGrade = 11;
    /// <summary>Fast mode's pruning slack between pieces: one grade XI materia.</summary>
    private const int FastSlackPoints = 36;
    /// <summary>Fast mode scores at most about this many candidates per food, the best by a linear proxy.</summary>
    private const int FastScoreLimit = 20_000;

    private sealed record PieceOption(int[] Gains, int Speed, List<MeldInput> Melds);
    /// <summary>Up to four stat gains, inline: millions of states per solve, no array each.</summary>
    private struct Gains4
    {
        public int G0, G1, G2, G3;
        public int this[int d]
        {
            readonly get => d switch { 0 => G0, 1 => G1, 2 => G2, _ => G3 };
            set { switch (d) { case 0: G0 = value; break; case 1: G1 = value; break; case 2: G2 = value; break; default: G3 = value; break; } }
        }
        public readonly int Sum(int n) { var t = 0; for (var d = 0; d < n; d++) t += this[d]; return t; }
        public readonly bool SameAs(Gains4 o, int n) { for (var d = 0; d < n; d++) if (this[d] != o[d]) return false; return true; }
    }

    private readonly record struct State(int Speed, Gains4 Gains, int Prev, int Option);

    private readonly record struct TierKey(int Crit, int DirectHit, int DirectHitBonus, int Determination, int Tenacity, int Speed)
    {
        public int Sum => Crit + DirectHit + DirectHitBonus + Determination + Tenacity;
        public bool Dominates(TierKey o) => Crit >= o.Crit && DirectHit >= o.DirectHit && DirectHitBonus >= o.DirectHitBonus
                                            && Determination >= o.Determination && Tenacity >= o.Tenacity;
    }

    public MeldSolution Solve(GearsetInput current, MeldSolveOptions? options = null)
    {
        options ??= new MeldSolveOptions();
        var watch = Stopwatch.StartNew();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };
        var workers = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Environment.ProcessorCount;
        var data = engine.Data;
        var job = data.Job(current.Job) ?? throw new ArgumentException($"Unknown job '{current.Job}'.");
        var usesPotion = (Sim.Rotations.RotationRegistry.For(job.Abbreviation)
                          ?? throw new ArgumentException($"{job.Abbreviation} has no rotation.")).UsesPotion;
        var medicine = usesPotion ? engine.BestMedicine(job.Abbreviation) : null;

        // Stats that raise damage, plus piety when asked for; the speed stat is the state key.
        var speedStat = model.Profile.SpeedStat == "sps" ? Stats.SpellSpeed : Stats.SkillSpeed;
        var dims = new List<string> { Stats.Critical, Stats.DirectHit, Stats.Determination };
        if (job.Role == "Tank") dims.Add(Stats.Tenacity);
        if (options.MinPiety != null) dims.Add(Stats.Piety);
        if (dims.Count > 4)
            throw new InvalidOperationException("The solver tracks at most four stats besides speed.");
        var n = dims.Count;
        var meldStats = dims.Append(speedStat).ToList();
        var tenacityIndex = dims.IndexOf(Stats.Tenacity);
        var pietyIndex = dims.IndexOf(Stats.Piety);

        double Score(GearsetInput g, out GearsetStats st, out DamageEstimate est)
        {
            st = engine.Compute(g);
            est = model.Evaluate(st, medicine != null ? engine.WithMedicine(st, medicine) : null);
            if (options.TargetGcdCs is int gcd && est.GcdCs != gcd) return double.MinValue;
            if (options.MinPiety is int minPiety && st.Final.GetValueOrDefault(Stats.Piety) < minPiety) return double.MinValue;
            return est.Dps;
        }
        var currentDps = model.Evaluate(engine.Compute(current), medicine != null ? engine.WithMedicine(engine.Compute(current), medicine) : null).Dps;

        // Baseline: the gear with only the melds that stay.
        var baselineInput = Clone(current);
        foreach (var (slot, piece) in baselineInput.Slots)
            if (!options.LockedSlots.Contains(slot) && !options.KeepExistingMelds)
                piece.Materia.Clear();

        // 1. Each piece's options.
        var slots = Slots.All.Where(baselineInput.Slots.ContainsKey).ToList();
        var pieceStats = engine.Compute(baselineInput).Slots.ToDictionary(r => r.Slot, StringComparer.Ordinal);
        var perPiece = new List<List<PieceOption>>();
        foreach (var slot in slots)
        {
            var piece = baselineInput.Slots[slot];
            var item = data.Item(piece.ItemId);
            if (item == null || options.LockedSlots.Contains(slot) || !pieceStats.TryGetValue(slot, out var report))
                perPiece.Add([new PieceOption(new int[dims.Count], 0, [])]);
            else
                perPiece.Add(PieceOptions(item, report.Stats, piece.Materia.Count, dims, speedStat, meldStats, options.Fast ? FastSlackPoints : 0));
        }

        // Fewest options first: the front grows slowest, and the piece with the most options goes
        // last, straight into tier vectors.
        var order = Enumerable.Range(0, slots.Count).OrderBy(i => perPiece[i].Count).ThenBy(i => i).ToList();
        slots = order.Select(i => slots[i]).ToList();
        perPiece = order.Select(i => perPiece[i]).ToList();

        // 2. Combine all but the last piece, pruning per exact speed total.
        var layers = new List<List<State>> { new() { new State(0, default, -1, -1) } };
        for (var p = 0; p < perPiece.Count - 1; p++)
        {
            var prev = layers[^1];
            var options_ = perPiece[p];
            // Expand in fixed chunks of the previous layer, each into its own speed buckets; merging
            // them in chunk order keeps the result independent of thread timing.
            var chunks = Chunks(prev.Count, workers);
            var partial = new Dictionary<int, List<State>>[chunks.Count];
            Parallel.For(0, chunks.Count, parallel, c =>
            {
                var local = new Dictionary<int, List<State>>();
                for (var i = chunks[c].Start; i < chunks[c].End; i++)
                {
                    for (var o = 0; o < options_.Count; o++)
                    {
                        var opt = options_[o];
                        var gains = prev[i].Gains;
                        for (var d = 0; d < n; d++)
                            gains[d] += opt.Gains[d];
                        var speed = prev[i].Speed + opt.Speed;
                        if (!local.TryGetValue(speed, out var bucket))
                            local[speed] = bucket = [];
                        bucket.Add(new State(speed, gains, i, o));
                    }
                }
                partial[c] = local;
            });
            var speeds = partial.SelectMany(d => d.Keys).Distinct().Order().ToList();
            var fronts = new List<State>[speeds.Count];
            Parallel.For(0, speeds.Count, parallel, b =>
            {
                var bucket = new List<State>();
                foreach (var part in partial)
                    if (part.TryGetValue(speeds[b], out var list))
                        bucket.AddRange(list);
                fronts[b] = ParetoFront(bucket, options.Fast ? FastSlackPoints : 0, n);
            });
            layers.Add(fronts.SelectMany(f => f).ToList());
            Trace($"layer {p}: {options_.Count} options, {prev.Count * options_.Count} -> {layers[^1].Count} states, {speeds.Count} speeds, {watch.ElapsedMilliseconds} ms");
        }

        // 3. The last piece into tier vectors, per food; score the survivors.
        var foods = options.SolveFood ? FoodCandidates(current, dims, speedStat) : [current.Food];
        var best = (Dps: double.MinValue, Prev: -1, Option: -1, Food: (FoodInput?)null);
        var scored = 0;
        var level = data.Snapshot.Levels.First(l => l.Level == current.Level);
        var last = perPiece[^1];
        var lastLayer = layers[^1];
        var allStats = meldStats.ToArray();
        foreach (var food in foods)
        {
            var foodInfo = food != null ? data.Food(food.ItemId) : null;
            var hq = food?.Hq ?? true;
            var useHq = hq && (foodInfo?.CanBeHq ?? false);
            var baseline = engine.Compute(WithFood(baselineInput, food));
            var preFood = allStats.Select(st => baseline.Final.GetValueOrDefault(st) - baseline.FoodBonus.GetValueOrDefault(st)).ToArray();
            var bonuses = allStats.Select(st => foodInfo?.Bonuses.FirstOrDefault(b => b.Stat == st)).ToArray();
            int Final(int d, int gain)
            {
                var v = preFood[d] + gain;
                var b = bonuses[d];
                return b == null ? v
                    : v + (b.Relative ? Math.Min(useHq ? b.MaxHq : b.Max, v * (useHq ? b.ValueHq : b.Value) / 100) : useHq ? b.ValueHq : b.Value);
            }

            var tierChunks = Chunks(lastLayer.Count, workers * 4);
            var tierParts = new Dictionary<TierKey, (int Prev, int Option)>[tierChunks.Count];
            Parallel.For(0, tierChunks.Count, parallel, c =>
            {
            var local = new Dictionary<TierKey, (int Prev, int Option)>();
            var g = new int[n];
            for (var i = tierChunks[c].Start; i < tierChunks[c].End; i++)
            {
                for (var o = 0; o < last.Count; o++)
                {
                    for (var d = 0; d < n; d++)
                        g[d] = lastLayer[i].Gains[d] + last[o].Gains[d];
                    if (options.MinPiety is int minPiety && Final(pietyIndex, g[pietyIndex]) < minPiety)
                        continue;
                    var speed = Final(dims.Count, lastLayer[i].Speed + last[o].Speed);
                    if (options.TargetGcdCs is int target && StatMath.GcdCentiseconds(level, speed, 2500, model.Profile.GcdHastePercent) != target)
                        continue;
                    var dh = Final(1, g[1]);
                    var key = new TierKey(
                        StatMath.Tier(200, Final(0, g[0]), level.BaseSubStat, level.LevelDiv),
                        StatMath.Tier(550, dh, level.BaseSubStat, level.LevelDiv),
                        StatMath.Tier(140, dh, level.BaseSubStat, level.LevelDiv),
                        StatMath.Tier(140, Final(2, g[2]), level.BaseMainStat, level.LevelDiv),
                        tenacityIndex >= 0 ? StatMath.Tier(112, Final(tenacityIndex, g[tenacityIndex]), level.BaseSubStat, level.LevelDiv) : 0,
                        StatMath.Tier(130, speed, level.BaseSubStat, level.LevelDiv));
                    local.TryAdd(key, (i, o));
                }
            }
            tierParts[c] = local;
            });
            // Merge in chunk order: the first combination reaching a tier vector keeps it.
            var byTiers = new Dictionary<TierKey, (int Prev, int Option)>(tierParts.Sum(t => t.Count));
            foreach (var part in tierParts)
                foreach (var (key, value) in part)
                    byTiers.TryAdd(key, value);
            // Front of the tier vectors per speed tier. The two DH tiers come from one DH value and
            // rise together, so their pair is a single rank; tenacity only counts for tanks.
            var dhPairs = byTiers.Keys.Select(k => (k.DirectHit, k.DirectHitBonus)).Distinct().Order().ToList();
            var dhRank = dhPairs.Select((pair, r) => (pair, r)).ToDictionary(x => x.pair, x => x.r);
            var tierN = tenacityIndex >= 0 ? 4 : 3;
            var groups = byTiers.GroupBy(kv => kv.Key.Speed).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
            var tierFronts = new List<(int Prev, int Option)>[groups.Count];
            Parallel.For(0, groups.Count, parallel, gi =>
            {
                var states = groups[gi].Select(kv => new State(0,
                    new Gains4 { G0 = kv.Key.Crit, G1 = dhRank[(kv.Key.DirectHit, kv.Key.DirectHitBonus)], G2 = kv.Key.Determination, G3 = kv.Key.Tenacity },
                    kv.Value.Prev, kv.Value.Option)).ToList();
                tierFronts[gi] = ParetoFront(states, 0, tierN).Select(st => (st.Prev, st.Option)).ToList();
            });
            var survivors = tierFronts.SelectMany(f => f).ToList();
            // Fast mode: score only the most promising candidates, ranked by a linear proxy (each stat
            // tier's damage value at the first candidate's stats).
            if (options.Fast && survivors.Count > FastScoreLimit)
            {
                var (p0, o0) = survivors[0];
                var reference = new List<(string, int)>();
                for (var d = 0; d < dims.Count; d++)
                    reference.Add((dims[d], lastLayer[p0].Gains[d] + last[o0].Gains[d]));
                reference.Add((speedStat, lastLayer[p0].Speed + last[o0].Speed));
                double DpsWith(string stat, int extra)
                {
                    var g = reference.Select(x => x.Item1 == stat ? (x.Item1, x.Item2 + extra) : x).ToList();
                    var st = engine.WithGearGains(baseline, foodInfo, hq, g);
                    return model.Evaluate(st, medicine != null ? engine.WithMedicine(st, medicine) : null).Dps;
                }
                var at = DpsWith(Stats.Critical, 0);
                // Points per tier: level divisor over the tier coefficient.
                double Weight(string stat, int k) => (DpsWith(stat, level.LevelDiv / k * 4) - at) / 4;
                var wCrit = Weight(Stats.Critical, 200);
                var wDh = (DpsWith(Stats.DirectHit, 100) - at) / 100;
                var wDet = Weight(Stats.Determination, 140);
                var wTen = tenacityIndex >= 0 ? Weight(Stats.Tenacity, 112) : 0;
                double Proxy((int Prev, int Option) c)
                {
                    double v = 0;
                    var g = lastLayer[c.Prev].Gains;
                    var o = last[c.Option].Gains;
                    v += wCrit * StatMath.Tier(200, Final(0, g[0] + o[0]), level.BaseSubStat, level.LevelDiv);
                    v += wDh * Final(1, g[1] + o[1]);
                    v += wDet * StatMath.Tier(140, Final(2, g[2] + o[2]), level.BaseMainStat, level.LevelDiv);
                    if (tenacityIndex >= 0)
                        v += wTen * StatMath.Tier(112, Final(tenacityIndex, g[tenacityIndex] + o[tenacityIndex]), level.BaseSubStat, level.LevelDiv);
                    return v;
                }
                // Keep the best per speed tier, so every GCD still gets a fair look.
                survivors = survivors.GroupBy(c => StatMath.Tier(130, Final(dims.Count, lastLayer[c.Prev].Speed + last[c.Option].Speed), level.BaseSubStat, level.LevelDiv))
                    .SelectMany(grp => grp.OrderByDescending(Proxy).Take(Math.Max(200, FastScoreLimit / Math.Max(1, survivors.Count / Math.Max(1, grp.Count())))))
                    .ToList();
            }
            Trace($"food {food?.ItemId}: {lastLayer.Count * last.Count} combinations, {byTiers.Count} tier vectors, {survivors.Count} scored, {watch.ElapsedMilliseconds} ms");

            // Scoring is independent per candidate: spread it over the cores, ties to the lowest index
            // so the result doesn't depend on scheduling.
            var dpsOf = new double[survivors.Count];
            Parallel.For(0, survivors.Count, parallel, k =>
            {
                var (prev, option) = survivors[k];
                var gains = new List<(string, int)>(allStats.Length);
                for (var d = 0; d < dims.Count; d++)
                    gains.Add((dims[d], lastLayer[prev].Gains[d] + last[option].Gains[d]));
                gains.Add((speedStat, lastLayer[prev].Speed + last[option].Speed));
                var stats = engine.WithGearGains(baseline, foodInfo, hq, gains);
                dpsOf[k] = model.Evaluate(stats, medicine != null ? engine.WithMedicine(stats, medicine) : null).Dps;
            });
            scored += survivors.Count;
            for (var k = 0; k < survivors.Count; k++)
                if (dpsOf[k] > best.Dps)
                    best = (dpsOf[k], survivors[k].Prev, survivors[k].Option, food);
        }

        if (best.Prev < 0)
            return new MeldSolution { Gear = current, Stats = engine.Compute(current), Dps = currentDps, CurrentDps = currentDps, Infeasible = true, Elapsed = watch.Elapsed };

        // Walk back through the layers for each piece's melds.
        var solved = WithFood(baselineInput, best.Food);
        solved.Slots[slots[^1]].Materia.AddRange(last[best.Option].Melds.Select(CloneMeld));
        var index = best.Prev;
        for (var p = perPiece.Count - 2; p >= 0; p--)
        {
            var state = layers[p + 1][index];
            solved.Slots[slots[p]].Materia.AddRange(perPiece[p][state.Option].Melds.Select(CloneMeld));
            index = state.Prev;
        }

        // 4. Local search: change one meld, or two at once (moving points between stats across
        // pieces), while it helps.
        var freeRefs = slots.Where(sl => !options.LockedSlots.Contains(sl))
            .SelectMany(sl => Enumerable.Range(baselineInput.Slots[sl].Materia.Count, solved.Slots[sl].Materia.Count - baselineInput.Slots[sl].Materia.Count)
                .Select(m => (Slot: sl, Index: m))).ToList();
        var (bestScore, solvedStats, solvedDps, swaps, searchRecomputed) = LocalSearch(solved, freeRefs, meldStats, Score, parallel, pairs: !options.Fast);

        return new MeldSolution
        {
            Gear = solved,
            Stats = solvedStats,
            Dps = solvedDps.Dps,
            SearchDps = best.Dps,
            SearchDpsRecomputed = searchRecomputed,
            CurrentDps = currentDps,
            GcdCs = solvedDps.GcdCs,
            CandidatesScored = scored,
            LocalSearchSwaps = swaps,
            Elapsed = watch.Elapsed,
        };
    }

    private delegate double Scorer(GearsetInput g, out GearsetStats stats, out DamageEstimate estimate);

    /// <summary>
    /// Moves: (meld, stat) singles, then (meld, stat, meld, stat) pairs when asked. Each sweep scores
    /// its moves in parallel on copies and applies the first one (in move order) that improves, until
    /// none does. Changes <paramref name="solved"/> in place.
    /// </summary>
    private static (double Score, GearsetStats Stats, DamageEstimate Estimate, int Swaps, double Start) LocalSearch(
        GearsetInput solved, List<(string Slot, int Index)> freeRefs, List<string> meldStats, Scorer score, ParallelOptions parallel, bool pairs)
    {
        var bestScore = score(solved, out var solvedStats, out var solvedDps);
        var start = bestScore;
        var swaps = 0;
        string StatAt(int x) => solved.Slots[freeRefs[x].Slot].Materia[freeRefs[x].Index].Stat;
        for (var improved = true; improved;)
        {
            improved = false;
            var singles = Enumerable.Range(0, freeRefs.Count)
                .SelectMany(x => meldStats.Where(st => st != StatAt(x)).Select(st => new[] { (x, st) })).ToList();
            var pairMoves = singles.Count == 0 || !pairs ? [] : Enumerable.Range(0, freeRefs.Count).SelectMany(x => Enumerable.Range(x + 1, freeRefs.Count - x - 1)
                .SelectMany(y => meldStats.Where(a => a != StatAt(x))
                    .SelectMany(a => meldStats.Where(b => b != StatAt(y)).Select(b => new[] { (x, a), (y, b) })))).ToList();
            foreach (var moves in new[] { singles, pairMoves })
            {
                var scores = new double[moves.Count];
                var results = new (GearsetStats Stats, DamageEstimate Estimate)[moves.Count];
                Parallel.For(0, moves.Count, parallel, k =>
                {
                    var trial = Clone(solved);
                    foreach (var (x, st) in moves[k])
                        trial.Slots[freeRefs[x].Slot].Materia[freeRefs[x].Index].Stat = st;
                    scores[k] = score(trial, out var tst, out var test);
                    results[k] = (tst, test);
                });
                var winner = Enumerable.Range(0, moves.Count).FirstOrDefault(k => scores[k] > bestScore + 1e-9, -1);
                if (winner < 0)
                    continue;
                foreach (var (x, st) in moves[winner])
                    solved.Slots[freeRefs[x].Slot].Materia[freeRefs[x].Index].Stat = st;
                (bestScore, solvedStats, solvedDps) = (scores[winner], results[winner].Stats, results[winner].Estimate);
                swaps++;
                improved = true;
                break;
            }
        }
        return (bestScore, solvedStats, solvedDps, swaps, start);
    }

    /// <summary>
    /// Improves the given melds without searching from scratch: single meld changes, and paired ones
    /// unless Fast is set (the stats only, slots and grades stay), until none helps. Milliseconds rather than a full
    /// solve; used to check whether a set is worth re-melding.
    /// </summary>
    public MeldSolution Polish(GearsetInput current, MeldSolveOptions? options = null)
    {
        options ??= new MeldSolveOptions();
        var watch = Stopwatch.StartNew();
        var job = engine.Data.Job(current.Job) ?? throw new ArgumentException($"Unknown job '{current.Job}'.");
        var medicine = (Sim.Rotations.RotationRegistry.For(job.Abbreviation)?.UsesPotion ?? false) ? engine.BestMedicine(job.Abbreviation) : null;
        var speedStat = model.Profile.SpeedStat == "sps" ? Stats.SpellSpeed : Stats.SkillSpeed;
        var meldStats = new List<string> { Stats.Critical, Stats.DirectHit, Stats.Determination };
        if (job.Role == "Tank") meldStats.Add(Stats.Tenacity);
        if (options.MinPiety != null) meldStats.Add(Stats.Piety);
        meldStats.Add(speedStat);
        double Score(GearsetInput g, out GearsetStats st, out DamageEstimate est)
        {
            st = engine.Compute(g);
            est = model.Evaluate(st, medicine != null ? engine.WithMedicine(st, medicine) : null);
            if (options.TargetGcdCs is int gcd && est.GcdCs != gcd) return double.MinValue;
            if (options.MinPiety is int minPiety && st.Final.GetValueOrDefault(Stats.Piety) < minPiety) return double.MinValue;
            return est.Dps;
        }
        var solved = Clone(current);
        var freeRefs = solved.Slots.Where(kv => !options.LockedSlots.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SelectMany(kv => Enumerable.Range(0, kv.Value.Materia.Count).Select(m => (Slot: kv.Key, Index: m))).ToList();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };
        var (_, stats, estimate, swaps, start) = LocalSearch(solved, freeRefs, meldStats, Score, parallel, pairs: !options.Fast);
        return new MeldSolution
        {
            Gear = solved, Stats = stats, Dps = estimate.Dps, SearchDps = start, SearchDpsRecomputed = start, CurrentDps = start,
            GcdCs = estimate.GcdCs, LocalSearchSwaps = swaps, Elapsed = watch.Elapsed,
        };
    }

    /// <summary>Distinct, non-dominated stat gains from filling a piece's free slots.</summary>
    private List<PieceOption> PieceOptions(GearItem item, Dictionary<string, int> stats, int usedSlots, List<string> dims,
        string speedStat, List<string> meldStats, int slack)
    {
        var data = engine.Data;
        var guaranteed = item.MateriaSlots;
        var total = item.AdvancedMelding ? GearRules.MaxMateriaSlots : guaranteed;
        var grades = Enumerable.Range(usedSlots, Math.Max(0, total - usedSlots))
            .Select(i => GearRules.GradeAllowedInSlot(FullGrade, i, guaranteed) ? FullGrade : OvermeldGrade).ToList();

        var options = new Dictionary<string, PieceOption>(StringComparer.Ordinal);
        foreach (var assignment in Assign(grades, meldStats))
        {
            var sums = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var m in assignment)
                sums[m.Stat] = sums.GetValueOrDefault(m.Stat) + (engine.MateriaValue(m) ?? 0);
            int Gain(string stat)
            {
                var sum = sums.GetValueOrDefault(stat);
                if (sum == 0) return 0;
                var cap = data.MeldCap(item, stat);
                return cap is int c ? Math.Clamp(c - stats.GetValueOrDefault(stat), 0, sum) : sum;
            }
            var option = new PieceOption(dims.Select(Gain).ToArray(), Gain(speedStat), assignment);
            options.TryAdd(string.Join(',', option.Gains) + "|" + option.Speed, option);
        }

        // Drop options another option beats or equals on every stat including speed (Fast mode: at
        // the same speed, also those within the slack of another on every stat).
        var list = options.Values.ToList();
        var front = list.Where(a => !list.Any(b => !ReferenceEquals(a, b) && b.Speed >= a.Speed
            && b.Gains.Zip(a.Gains).All(x => x.First >= x.Second) && (b.Speed > a.Speed || b.Gains.Zip(a.Gains).Any(x => x.First > x.Second)))).ToList();
        if (slack == 0)
            return front;
        var kept = new List<PieceOption>();
        foreach (var a in front.OrderByDescending(o => o.Gains.Sum()))
            if (!kept.Any(b => b.Speed == a.Speed && b.Gains.Zip(a.Gains).All(x => x.First + slack >= x.Second)))
                kept.Add(a);
        return kept;
    }

    /// <summary>Every multiset of stats for runs of slots that take the same grade, in slot order.</summary>
    private static IEnumerable<List<MeldInput>> Assign(List<int> grades, List<string> stats)
    {
        if (grades.Count == 0)
        {
            yield return [];
            yield break;
        }
        var runLength = grades.TakeWhile(g => g == grades[0]).Count();
        foreach (var combo in Multisets(stats, runLength))
            foreach (var rest in Assign(grades.Skip(runLength).ToList(), stats))
                yield return [.. combo.Select(s => new MeldInput { Stat = s, Grade = grades[0] }), .. rest];
    }

    private static IEnumerable<List<string>> Multisets(List<string> items, int count, int start = 0)
    {
        if (count == 0)
        {
            yield return [];
            yield break;
        }
        for (var i = start; i < items.Count; i++)
            foreach (var rest in Multisets(items, count - 1, i))
                yield return [items[i], .. rest];
    }

    private static List<State> ParetoFront(List<State> states, int slack, int n)
    {
        if (states.Count == 0)
            return states;
        return n switch
        {
            3 => ParetoFront3(states, slack),
            4 => ParetoFront4(states, slack),
            _ => ParetoFrontGeneral(states, slack, n),
        };
    }

    private static List<State> SortedDistinct(List<State> states)
    {
        var sorted = states.OrderByDescending(s => s.Gains.G0).ThenByDescending(s => s.Gains.G1)
            .ThenByDescending(s => s.Gains.G2).ThenByDescending(s => s.Gains.G3).ToList();
        var result = new List<State>(sorted.Count);
        foreach (var s in sorted)
            if (result.Count == 0 || !result[^1].Gains.SameAs(s.Gains, 4))
                result.Add(s);
        return result;
    }

    /// <summary>[start, end) ranges splitting <paramref name="count"/> items into about <paramref name="parts"/> chunks.</summary>
    private static List<(int Start, int End)> Chunks(int count, int parts)
    {
        var size = Math.Max(1, (count + parts - 1) / Math.Max(1, parts));
        var result = new List<(int, int)>();
        for (var start = 0; start < count; start += size)
            result.Add((start, Math.Min(count, start + size)));
        return result;
    }

    /// <summary>
    /// Front in three dimensions in O(n log n): in (d0, d1, d2) descending order a state is
    /// dominated exactly when an earlier one has d1 and d2 at least as high (within the slack), which
    /// a Fenwick tree over d1 answers with the highest d2 among kept states at or above it.
    /// </summary>
    private static List<State> ParetoFront3(List<State> states, int slack)
    {
        var sorted = SortedDistinct(states);
        var maxD1 = sorted.Max(s => s.Gains.G1);
        var tree = new int[maxD1 + 2];
        Array.Fill(tree, int.MinValue);
        // Index i stands for d1 = maxD1 - i, so a prefix covers every d1 at or above a value.
        int Query(int d1)
        {
            var best = int.MinValue;
            for (var i = Math.Min(maxD1 - d1 + 1, tree.Length - 1); i > 0; i -= i & -i)
                best = Math.Max(best, tree[i]);
            return best;
        }
        void Insert(int d1, int d2)
        {
            for (var i = maxD1 - d1 + 1; i < tree.Length; i += i & -i)
                tree[i] = Math.Max(tree[i], d2);
        }
        var kept = new List<State>();
        foreach (var s in sorted)
        {
            if (Query(Math.Max(0, s.Gains.G1 - slack)) >= s.Gains.G2 - slack)
                continue;
            kept.Add(s);
            Insert(s.Gains.G1, s.Gains.G2);
        }
        return kept;
    }

    /// <summary>
    /// Front in four dimensions in O(n log² n): the same sweep over d0, with an offline 2D Fenwick
    /// tree (d1 outer, d2 inner) holding the highest d3 among kept states at or above (d1, d2).
    /// </summary>
    private static List<State> ParetoFront4(List<State> states, int slack)
    {
        var sorted = SortedDistinct(states);
        // Ranks in descending order, 1-based: a prefix of ranks covers every value at or above.
        var d1Values = sorted.Select(s => s.Gains.G1).Distinct().OrderByDescending(v => v).ToArray();
        var d2Values = sorted.Select(s => s.Gains.G2).Distinct().OrderByDescending(v => v).ToArray();
        int RankAtLeast(int[] desc, int v)
        {
            // Number of values >= v.
            int lo = 0, hi = desc.Length;
            while (lo < hi) { var mid = (lo + hi) / 2; if (desc[mid] >= v) lo = mid + 1; else hi = mid; }
            return lo;
        }
        int Rank(int[] desc, int v) => RankAtLeast(desc, v);
        var n1 = d1Values.Length;
        // Each outer node lists the d2 ranks of the points that can ever be inserted into it.
        var lists = new List<int>[n1 + 1];
        for (var i = 1; i <= n1; i++) lists[i] = [];
        foreach (var s in sorted)
            for (var i = Rank(d1Values, s.Gains.G1); i <= n1; i += i & -i)
                lists[i].Add(Rank(d2Values, s.Gains.G2));
        var keys = new int[n1 + 1][];
        var inner = new int[n1 + 1][];
        for (var i = 1; i <= n1; i++)
        {
            keys[i] = lists[i].Distinct().Order().ToArray();
            inner[i] = new int[keys[i].Length + 1];
            Array.Fill(inner[i], int.MinValue);
        }
        int Query(int r1, int r2)
        {
            var best = int.MinValue;
            for (var i = r1; i > 0; i -= i & -i)
            {
                // Inner prefix over the keys <= r2.
                var k = keys[i];
                int lo = 0, hi = k.Length;
                while (lo < hi) { var mid = (lo + hi) / 2; if (k[mid] <= r2) lo = mid + 1; else hi = mid; }
                for (var j = lo; j > 0; j -= j & -j)
                    best = Math.Max(best, inner[i][j]);
            }
            return best;
        }
        void Insert(int r1, int r2, int d3)
        {
            for (var i = r1; i <= n1; i += i & -i)
            {
                var j = Array.BinarySearch(keys[i], r2) + 1;
                for (; j < inner[i].Length; j += j & -j)
                    inner[i][j] = Math.Max(inner[i][j], d3);
            }
        }
        var kept = new List<State>();
        foreach (var s in sorted)
        {
            var r1 = RankAtLeast(d1Values, s.Gains.G1 - slack);
            var r2 = RankAtLeast(d2Values, s.Gains.G2 - slack);
            if (r1 > 0 && r2 > 0 && Query(r1, r2) >= s.Gains.G3 - slack)
                continue;
            kept.Add(s);
            Insert(Rank(d1Values, s.Gains.G1), Rank(d2Values, s.Gains.G2), s.Gains.G3);
        }
        return kept;
    }

    private static List<State> ParetoFrontGeneral(List<State> states, int slack, int n)
    {
        var sorted = states.OrderByDescending(s => s.Gains.Sum(n)).ToList();
        var kept = new List<State>();
        foreach (var s in sorted)
        {
            var dominated = false;
            foreach (var k in kept)
            {
                var all = true;
                for (var d = 0; d < n && all; d++)
                    all = k.Gains[d] + slack >= s.Gains[d];
                if (all) { dominated = true; break; }
            }
            if (!dominated)
                kept.Add(s);
        }
        return kept;
    }

    /// <summary>The current tier's foods (highest item level) whose stats all matter to the job.</summary>
    private List<FoodInput?> FoodCandidates(GearsetInput current, List<string> dims, string speedStat)
    {
        var useful = dims.Append(speedStat).Append(Stats.Vitality).ToHashSet(StringComparer.Ordinal);
        var foods = engine.Data.Snapshot.Foods.Where(f => f.Bonuses.All(b => useful.Contains(b.Stat))).ToList();
        if (foods.Count == 0)
            return [current.Food];
        var top = foods.Max(f => f.ItemLevel);
        return foods.Where(f => f.ItemLevel == top).Select(f => (FoodInput?)new FoodInput { ItemId = f.ItemId, Hq = true }).ToList();
    }

    private static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("RMC_SOLVER_TRACE") != null)
            Console.Error.WriteLine("  " + message);
    }

    private static MeldInput CloneMeld(MeldInput m) => new() { Stat = m.Stat, Grade = m.Grade };

    private static GearsetInput WithFood(GearsetInput input, FoodInput? food)
    {
        var copy = Clone(input);
        copy.Food = food;
        return copy;
    }

    private static GearsetInput Clone(GearsetInput input) => new()
    {
        Job = input.Job,
        Level = input.Level,
        ClanId = input.ClanId,
        PartyBonus = input.PartyBonus,
        Food = input.Food,
        Slots = input.Slots.ToDictionary(kv => kv.Key, kv => new PieceInput
        {
            ItemId = kv.Value.ItemId,
            Hq = kv.Value.Hq,
            Materia = kv.Value.Materia.Select(CloneMeld).ToList(),
            RelicStats = kv.Value.RelicStats,
        }, StringComparer.Ordinal),
    };
}
