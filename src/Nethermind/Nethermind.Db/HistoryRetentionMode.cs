// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

/// <summary>How captured flat history is retained. The mode, not the block count, selects the on-disk row format.</summary>
public enum HistoryRetentionMode
{
    /// <summary>Unbounded retention from genesis or the pivot, never pruned. Not "no history" - that is <c>HistoryEnabled=false</c>.</summary>
    None,

    /// <summary>A window of the most recent <c>HistoryRetentionBlocks</c> blocks; the pruner reclaims below it as the watermark advances.</summary>
    Rolling,

    /// <summary>Everything from <c>HistoryRetentionSinceBlock</c> onward, forever: capture starts there, the floor is
    /// published there once and never moves, and nothing is ever pruned.</summary>
    SinceBlock,
}
