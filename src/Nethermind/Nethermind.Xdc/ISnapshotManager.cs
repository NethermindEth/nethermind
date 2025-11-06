// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;
public interface ISnapshotManager
{
    static bool IsTimeforSnapshot(long blockNumber, IXdcReleaseSpec spec)
    {
        if (blockNumber == spec.SwitchBlock)
            return true;
        return blockNumber % spec.EpochLength == spec.EpochLength - spec.Gap;
    }
    Snapshot? GetSnapshot(long blockNumber, IXdcReleaseSpec spec);
    void StoreSnapshot(Snapshot snapshot);
    (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(long blockNumber, Hash256 parentHash, IXdcReleaseSpec spec);
}
