// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class StorageRootMoveCheck(StoragePresenceProbe probe, MismatchSink mismatches)
{
    public void OnMoved(in ValueHash256 accountPath, ulong block, in ValueHash256 previous, in ValueHash256 current)
    {
        if (probe.HasSlotRows(accountPath)) return;

        mismatches.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingSlotHistory, previous, current));
    }

    public void OnAnchor(in ValueHash256 accountPath, ulong block, in ValueHash256 root)
    {
        if (root == Keccak.EmptyTreeHash.ValueHash256 || probe.HasSlotRows(accountPath)) return;

        mismatches.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingSlotHistory, root, default));
    }
}
