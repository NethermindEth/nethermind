// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

/// <summary>
/// Bounds of the persisted state window. Implemented by <see cref="IWorldStateManager"/>;
/// each backend (trie / flat) keeps the persisted values co-located with its state DB so
/// wiping the state directory drops the bounds along with the state.
/// </summary>
public interface IStateBoundary
{
    /// <summary>
    /// Absolute lower bound of the persisted state window. Updated when fast/snap sync
    /// completes (= pivot) and after a full pruning run completes (= copied state's block).
    /// Null if never set (archive node syncing from genesis).
    /// </summary>
    /// <remarks>
    /// Monotonically non-decreasing while non-null — setting a smaller value is a no-op.
    /// Setting to <c>null</c> is allowed as an explicit reset (e.g. when wiping a corrupt
    /// state DB), so callers should not rely on equality with a previously-set value.
    /// </remarks>
    long? OldestStateBlock { get; set; }

    /// <summary>
    /// Configured rolling-window retention in blocks (e.g. trie memory pruning). Null when
    /// there is no rolling window (archive, full pruning, flat storage); the absolute floor
    /// is reported via <see cref="OldestStateBlock"/> instead.
    /// </summary>
    long? RetentionWindowBlocks { get; }
}
