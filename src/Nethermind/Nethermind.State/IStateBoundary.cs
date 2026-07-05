// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;

namespace Nethermind.State;

/// <summary>
/// Read-only view of the persisted state window. Registered per-backend (trie: a co-located
/// metadata store; flat: the persistence manager) rather than off <see cref="IWorldStateManager"/>,
/// so it can be injected into components built before the manager (e.g. the block tree).
/// </summary>
public interface IStateBoundary
{
    /// <summary>
    /// Absolute lower bound of the persisted state window. Updated when fast/snap sync
    /// completes (= pivot) and after a full pruning run completes (= copied state's block).
    /// Null if never set (archive node syncing from genesis).
    /// </summary>
    ulong? OldestStateBlock { get; }

    /// <summary>
    /// Configured rolling-window retention in blocks (e.g. trie memory pruning). Null when
    /// there is no rolling window (archive, full pruning, flat storage); the absolute floor
    /// is reported via <see cref="OldestStateBlock"/> instead.
    /// </summary>
    ulong? RetentionWindowBlocks { get; }

    /// <summary>
    /// Highest block whose state is durably persisted; null when unknown (fresh node or still
    /// syncing). The ceiling counterpart to the <see cref="OldestStateBlock"/> floor.
    /// </summary>
    ulong? BestPersistedState { get; }

    /// <summary>
    /// <see cref="BestPersistedState"/> together with the state root it was persisted with, for
    /// backends that track it. After an unclean shutdown a backend that cannot roll back (flat)
    /// can hold persisted state ahead of the block tree head; recovery fast-forwards the head onto
    /// the block matching this pair instead of re-executing the gap. Backends that only track the
    /// number (trie — state exists at every in-window root, so re-execution recovers) return false.
    /// </summary>
    bool TryGetBestPersistedState(out ulong blockNumber, [NotNullWhen(true)] out Hash256? stateRoot);
}

/// <summary>
/// Write side of the state-availability floor. Trie-specific — flat state tracking is handled
/// directly by the persistence manager and exposes no writer.
/// </summary>
/// <remarks>
/// Held by <c>PatriciaTreeSyncStore.FinalizeSync</c> (advancing the floor to the synced pivot)
/// and <c>FullPruner</c> (advancing it to the copied state's block on a successful prune).
/// Monotonically non-decreasing while non-null — setting a smaller value is a no-op. Setting to
/// <c>null</c> is allowed as an explicit reset (e.g. when wiping a corrupt state DB).
/// </remarks>
public interface IStateBoundaryWriter
{
    ulong? OldestStateBlock { set; }
}

/// <summary>Empty boundary for construction sites with no state backend (e.g. simulated block trees).</summary>
public sealed class NullStateBoundary : IStateBoundary
{
    public static readonly NullStateBoundary Instance = new();

    private NullStateBoundary() { }

    public ulong? OldestStateBlock => null;
    public ulong? RetentionWindowBlocks => null;
    public ulong? BestPersistedState => null;

    public bool TryGetBestPersistedState(out ulong blockNumber, [NotNullWhen(true)] out Hash256? stateRoot)
    {
        blockNumber = 0;
        stateRoot = null;
        return false;
    }
}
