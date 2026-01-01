// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

public interface ISnapshotManager
{
    static bool IsTimeforSnapshot(ulong blockNumber, IXdcReleaseSpec spec)
    {
        if (blockNumber == spec.SwitchBlock)
            return true;

        ulong epochLength = (ulong)spec.EpochLength;
        ulong gap = (ulong)spec.Gap;
        return blockNumber % epochLength == epochLength - gap;
    }
    Snapshot? GetSnapshotByGapNumber(ulong gapNumber);
    Snapshot? GetSnapshotByBlockNumber(ulong blockNumber, IXdcReleaseSpec spec);
    void StoreSnapshot(Snapshot snapshot);
    (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(ulong blockNumber, Hash256 parentHash, IXdcReleaseSpec spec);
}
