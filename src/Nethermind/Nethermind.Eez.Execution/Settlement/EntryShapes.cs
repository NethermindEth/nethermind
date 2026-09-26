// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Execution.Settlement;

public static class EntryShapes
{
    public static EntryShape Classify(ExecutionEntry entry, StateUpdate update, ulong rollupId)
    {
        if (!entry.Success || entry.ExpectedCalls.Length != 0)
        {
            return EntryShape.Invalid;
        }

        if (entry.ProxyEntryHash != default)
        {
            return entry.Calls.Length == 0 ? EntryShape.Inbound : EntryShape.Invalid;
        }

        if (entry.Calls.Length != 0)
        {
            return entry.Calls is [{ RevertNextNCalls: 0, IsStatic: false, Gas: 0 }] ? EntryShape.Outbound : EntryShape.Invalid;
        }

        return entry.DestinationRollupId == rollupId
            && entry.ReturnData.Length == 0
            && entry.RollingHash == RollingHash.SeedL1([new StateCommitment(update.RollupId, update.CurrentState)], default)
                ? EntryShape.Anchor
                : EntryShape.Invalid;
    }
}
