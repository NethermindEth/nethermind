// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

/// <summary>
/// Read-only view of the persisted state window. Registered per backend so it can be
/// injected into components built before the world-state manager.
/// </summary>
public interface IStateBoundary
{
    /// <summary>
    /// Absolute lower bound of the persisted state window. Null if never set.
    /// </summary>
    ulong? OldestStateBlock { get; }

    /// <summary>
    /// Configured rolling-window retention in blocks, or null when no rolling window is configured.
    /// </summary>
    ulong? RetentionWindowBlocks { get; }

    /// <summary>
    /// Highest block whose state is durably persisted, or null when unknown.
    /// </summary>
    ulong? BestPersistedState { get; }
}
/// <summary>Empty boundary for construction sites with no state backend.</summary>
public sealed class NullStateBoundary : IStateBoundary
{
    public static readonly NullStateBoundary Instance = new();

    private NullStateBoundary() { }

    public ulong? OldestStateBlock => null;
    public ulong? RetentionWindowBlocks => null;
    public ulong? BestPersistedState => null;
}
