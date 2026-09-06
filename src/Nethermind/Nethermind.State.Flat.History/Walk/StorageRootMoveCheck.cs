// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class StorageRootMoveCheck(StoragePresenceProbe probe, List<HistoryWalkMismatch> mismatches)
{
    public void OnMoved(in ValueHash256 accountPath, ulong block, in ValueHash256 previous, in ValueHash256 current)
    {
        if (probe.HasSlotRows(accountPath)) return;

        mismatches.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingSlotHistory, previous, current));
    }
}
