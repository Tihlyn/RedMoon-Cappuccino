using Rmc.Engine.Damage;
using Rmc.Engine.Melds;
using Rmc.GameData;

namespace Rmc.Engine.Upgrades;

/// <summary>Reference starting gear for planner tests and demos.</summary>
public static class StartingSets
{
    /// <summary>
    /// A fresh max-level set: the best crafted piece the job can wear in each of the target's slots
    /// (or the target's own piece where nothing is crafted), the target's food, melds solved.
    /// </summary>
    public static GearsetInput Crafted(StatEngine engine, FittedProfileModel model, GearsetInput target)
    {
        var data = engine.Data;
        var start = new GearsetInput
        {
            Job = target.Job, Level = target.Level, ClanId = target.ClanId, PartyBonus = target.PartyBonus, Food = target.Food,
        };
        foreach (var (slot, piece) in target.Slots)
        {
            var goal = data.Item(piece.ItemId);
            var crafted = goal == null ? null : data.Snapshot.Items
                .Where(i => i.Crafted && i.Slot == goal.Slot && i.Jobs.Contains(target.Job) && i.ItemLevel < goal.ItemLevel)
                .OrderByDescending(i => i.ItemLevel).ThenBy(i => i.Id).FirstOrDefault();
            start.Slots[slot] = crafted != null ? new PieceInput { ItemId = crafted.Id } : new PieceInput { ItemId = piece.ItemId, RelicStats = piece.RelicStats };
        }
        return new MeldSolver(engine, model).Solve(start, new MeldSolveOptions { Fast = true }).Gear;
    }
}
