// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateDiffsWriter.Storage;

/// <summary>
/// Column families for the <c>BlockDiffs</c> database: <see cref="Default"/> holds per-block records
/// (pruned), <see cref="SlotCounts"/> holds the per-account running slot count (never pruned).
/// </summary>
public enum BlockDiffsColumns
{
    Default,
    SlotCounts,
}
