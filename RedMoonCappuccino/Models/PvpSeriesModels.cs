using System;
using System.Collections.Generic;

namespace RedMoonCappuccino.Models;

/// <summary>Why a <see cref="PvpSeriesSnapshot"/> carries no usable series data.</summary>
public enum PvpSeriesStatus
{
    /// <summary>Series level, EXP and currencies were all read.</summary>
    Ok,

    /// <summary>No character is logged in.</summary>
    NotLoggedIn,

    /// <summary>
    /// Logged in, but the server has not populated the client's PvP profile yet.
    /// Happens for a moment right after login.
    /// </summary>
    ProfileNotLoaded,

    /// <summary>The read threw; the log has the exception.</summary>
    Failed,
}

/// <summary>
/// One read of the player's PvP profile. Taken when the main window opens (and on
/// demand), never per frame, so the UI shows exactly what was captured and when.
/// </summary>
public sealed record PvpSeriesSnapshot
{
    public PvpSeriesStatus Status { get; init; }

    /// <summary>Series number as the game counts it (Series 12, ...).</summary>
    public int Series { get; init; }

    /// <summary>Current series level.</summary>
    public int Rank { get; init; }

    /// <summary>Highest level whose reward has been collected from the PvP Profile window.</summary>
    public int ClaimedRank { get; init; }

    /// <summary>Series EXP earned within the current level, not the running total.</summary>
    public int Experience { get; init; }

    public int WolfMarks { get; init; }
    public int TrophyCrystals { get; init; }

    public DateTime CapturedAtUtc { get; init; }

    public bool IsAvailable => Status == PvpSeriesStatus.Ok;

    public static PvpSeriesSnapshot Unavailable(PvpSeriesStatus status) =>
        new() { Status = status, CapturedAtUtc = DateTime.UtcNow };
}

/// <summary>Series EXP a PvP mode awards per match, by finishing position.</summary>
/// <param name="Name">Player-facing mode name.</param>
/// <param name="Note">Tooltip shown on the mode name, or null.</param>
/// <param name="First">EXP for a win / 1st place.</param>
/// <param name="Second">EXP for 2nd place; null for two-team modes.</param>
/// <param name="Third">EXP for a loss / 3rd place.</param>
public sealed record PvpModeExp(string Name, string? Note, int First, int? Second, int Third);

/// <summary>Matches of one mode needed to close the EXP gap, by finishing position.</summary>
public sealed record PvpModeRequirement(PvpModeExp Mode, int First, int? Second, int Third);

/// <summary>What the calculator says about getting from a snapshot to the target rank.</summary>
public sealed class PvpSeriesPlan
{
    public int TargetRank { get; init; }

    /// <summary>EXP needed to move from the current level to the next one.</summary>
    public int ExpToNextLevel { get; init; }

    /// <summary>Running total: EXP for every completed level plus progress in the current one.</summary>
    public long CurrentTotalExp { get; init; }

    /// <summary>Running total required to have reached <see cref="TargetRank"/>.</summary>
    public long TargetTotalExp { get; init; }

    /// <summary>Zero once the target rank is reached.</summary>
    public long RemainingExp { get; init; }

    public bool TargetReached => RemainingExp == 0;

    public IReadOnlyList<PvpModeRequirement> Requirements { get; init; } = Array.Empty<PvpModeRequirement>();
}
