// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class HistoryRetentionConstants
{
    public const ulong MinEpochsForBlockRequests = 33024;

    public const ulong WeakSubjectivityPeriodEpochs = 3533;

    /// <summary>The consensus layer's history serving window from the Gloas fork (<c>HISTORY_PRUNE_EPOCHS</c>).</summary>
    public const ulong HistoryPruneEpochs = 14299;
}
