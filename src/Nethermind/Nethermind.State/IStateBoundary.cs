// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

/// <summary>
/// Read-only view of the persisted state window. Implemented by <see cref="IWorldStateManager"/>;
/// each backend (trie / flat) keeps the persisted values co-located with its state DB so
/// wiping the state directory drops the bounds along with the state.
/// </summary>
/// <remarks>
/// Consumers that only need to report the window (e.g. <c>eth_capabilities</c>) should depend on
/// this interface. Components that advance the floor — <c>StateSyncRunner</c>, <c>FullPruner</c> —
/// depend on <see cref="IStateBoundaryWriter"/> instead so the write surface stays narrow.
/// </remarks>
public interface IStateBoundary
{
    /// <summary>
    /// Absolute lower bound of the persisted state window. Updated when fast/snap sync
    /// completes (= pivot) and after a full pruning run completes (= copied state's block).
    /// Null if never set (archive node syncing from genesis).
    /// </summary>
    long? OldestStateBlock { get; }

    /// <summary>
    /// Configured rolling-window retention in blocks (e.g. trie memory pruning). Null when
    /// there is no rolling window (archive, full pruning, flat storage); the absolute floor
    /// is reported via <see cref="OldestStateBlock"/> instead.
    /// </summary>
    long? RetentionWindowBlocks { get; }
}

/// <summary>
/// Write side of <see cref="IStateBoundary"/>. Held only by the components that legitimately
/// advance the floor.
/// </summary>
public interface IStateBoundaryWriter
{
    /// <summary>
    /// Absolute lower bound of the persisted state window. Monotonically non-decreasing while
    /// non-null — setting a smaller value is a no-op. Setting to <c>null</c> is allowed as an
    /// explicit reset (e.g. when wiping a corrupt state DB).
    /// </summary>
    long? OldestStateBlock { set; }
}
