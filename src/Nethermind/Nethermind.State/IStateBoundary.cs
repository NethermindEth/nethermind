// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

/// <summary>
/// Read-only view of the persisted state window. Registered with the active backend rather than off
/// <see cref="IWorldStateManager"/>, so it can be injected into components built before the manager
/// (e.g. the block tree).
/// </summary>
public interface IStateBoundary
{
    /// <summary>
    /// Absolute lower bound of the persisted state window. Null when unknown. Separately retained
    /// historical data does not extend this state availability boundary.
    /// </summary>
    ulong? OldestStateBlock { get; }

    /// <summary>
    /// Highest block whose state is durably persisted; null when unknown (fresh node or still
    /// syncing). The ceiling counterpart to the <see cref="OldestStateBlock"/> floor.
    /// </summary>
    ulong? BestPersistedState { get; }
}
/// <summary>Empty boundary for construction sites with no state backend.</summary>
public sealed class NullStateBoundary : IStateBoundary
{
    public static readonly NullStateBoundary Instance = new();

    private NullStateBoundary() { }

    public ulong? OldestStateBlock => null;
    public ulong? BestPersistedState => null;
}
