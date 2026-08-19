using System;
using System.Collections.Concurrent;
using System.IO;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>
/// Remembers what a policy decided in a state, so the same position is never reasoned about twice.
///
/// <para>Every policy here is a pure function of the state once its opener is spent: the same
/// durability, CP, buffs, charges and condition always produce the same choice. Crafts share their
/// early positions heavily — thousands of trials all start from the same opening and diverge only
/// as the conditions roll — so the same handful of states is re-evaluated constantly, and each
/// re-evaluation re-runs a full candidate sweep with a simulated apply per action per condition.</para>
///
/// <para>Keyed on the whole state rather than a summary. A partial key would collapse positions
/// that differ in something the policy reads, and a cache that returns the right answer for the
/// wrong position is worse than no cache: it would be indistinguishable from a scoring bug, which
/// is the exact failure this phase has spent its time chasing.</para>
///
/// <para>Shared across trials and safe to share across threads — but <strong>never across
/// policies</strong>. The key is the position, not the position and who is looking at it, so two
/// policies sharing one cache answer each other's questions. A smoke run caught exactly that: the
/// router and the expectimax reported byte-identical results because the second was reading the
/// first's decisions. Each policy owns its cache, and <see cref="Owner"/> is stamped into the file
/// so a saved one cannot be loaded by the wrong policy either.</para>
/// </summary>
public sealed class DecisionCache
{
    private readonly ConcurrentDictionary<CraftState, CraftAction> entries = new();

    /// <summary>Which policy's decisions these are. A cache is only valid for the one that filled it.</summary>
    public string Owner { get; }

    public DecisionCache(string owner) => Owner = owner;

    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public int Count => entries.Count;

    public bool TryGet(CraftState state, out CraftAction action) => entries.TryGetValue(state, out action);

    public void Store(CraftState state, CraftAction action) => entries[state] = action;

    /// <summary>Look up, or compute and remember. Counters are approximate under contention, which is fine for a report.</summary>
    public CraftAction GetOrAdd(CraftState state, System.Func<CraftState, CraftAction> compute)
    {
        if (entries.TryGetValue(state, out var cached)) { Hits++; return cached; }

        Misses++;
        var action = compute(state);
        if (entries.Count < Capacity) entries[state] = action;
        return action;
    }

    public void Clear()
    {
        entries.Clear();
        Hits = 0;
        Misses = 0;
    }

    /// <summary>
    /// Ceiling on retained states. A long discovery run reaches millions of distinct positions and
    /// would otherwise take the machine's memory with it; past this the cache stops growing and
    /// keeps serving what it already learned, which is where the hits are anyway.
    /// </summary>
    public const int Capacity = 12_000_000;

    public bool AtCapacity => entries.Count >= Capacity;

    /// <summary>
    /// Writes the cache in a packed binary form.
    ///
    /// <para>Persisted because the point of a long run is that the next run does not repeat it. A
    /// discovery pass that produces a cache which dies with its process has bought nothing but a
    /// report.</para>
    /// </summary>
    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(Version);
        writer.Write(Owner);
        writer.Write(entries.Count);

        foreach (var (state, action) in entries)
        {
            writer.Write(state.Progress);
            writer.Write(state.Quality);
            writer.Write(state.Durability);
            writer.Write(state.Cp);
            writer.Write(state.Step);
            writer.Write((byte)state.Condition);
            writer.Write(state.InnerQuiet);
            writer.Write(state.BuffTimers);
            writer.Write(state.CarefulObservationLeft);
            writer.Write(state.HeartAndSoulLeft);
            writer.Write(state.QuickInnovationLeft);
            writer.Write(state.TrainedPerfectionLeft);
            writer.Write(state.GamblesUsed);
            writer.Write(state.MendsUsed);
            writer.Write(state.HeartAndSoulActive);
            writer.Write(state.TrainedPerfectionActive);
            writer.Write((byte)state.PreviousAction);
            writer.Write(state.Completed);
            writer.Write(state.Failed);
            writer.Write((byte)action);
        }
    }

    /// <summary>Loads a saved cache, ignoring one written by a different layout.</summary>
    public int Load(string path)
    {
        if (!File.Exists(path)) return 0;

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        // A cache keyed on a state layout that has since changed would answer for positions it
        // never saw. Discarding it is the only safe reading of a version mismatch.
        if (reader.ReadInt32() != Version) return 0;

        // Decisions made by a different policy are not this policy's decisions.
        if (reader.ReadString() != Owner) return 0;

        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var state = new CraftState
            {
                Progress = reader.ReadInt32(),
                Quality = reader.ReadInt32(),
                Durability = reader.ReadInt32(),
                Cp = reader.ReadInt32(),
                Step = reader.ReadInt32(),
                Condition = (CraftCondition)reader.ReadByte(),
                InnerQuiet = reader.ReadByte(),
                BuffTimers = reader.ReadUInt64(),
                CarefulObservationLeft = reader.ReadByte(),
                HeartAndSoulLeft = reader.ReadByte(),
                QuickInnovationLeft = reader.ReadByte(),
                TrainedPerfectionLeft = reader.ReadByte(),
                GamblesUsed = reader.ReadByte(),
                MendsUsed = reader.ReadByte(),
                HeartAndSoulActive = reader.ReadBoolean(),
                TrainedPerfectionActive = reader.ReadBoolean(),
                PreviousAction = (CraftAction)reader.ReadByte(),
                Completed = reader.ReadBoolean(),
                Failed = reader.ReadBoolean(),
            };
            entries[state] = (CraftAction)reader.ReadByte();
        }

        return entries.Count;
    }

    /// <summary>Bumped whenever the state layout changes, so a stale file is discarded rather than trusted.</summary>
    private const int Version = 1;

    public string Summarise()
    {
        var total = Hits + Misses;
        var rate = total == 0 ? 0 : Hits * 100.0 / total;
        return $"{Owner}: {Count:N0} states, {rate:0.0}% hit rate ({Hits:N0} hits / {Misses:N0} misses)";
    }
}
