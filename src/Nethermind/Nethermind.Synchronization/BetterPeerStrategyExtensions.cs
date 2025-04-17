// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Synchronization;

public static class BetterPeerStrategyExtensions
{
    public static int Compare(this IBetterPeerStrategy peerStrategy, BlockHeader? header, ISyncPeer? peerInfo)
    {
        UInt256 headerDifficulty = header?.TotalDifficulty ?? UInt256.Zero;
        long headerNumber = header?.Number ?? 0;

        UInt256 peerDifficulty = peerInfo?.TotalDifficulty ?? UInt256.Zero;
        long peerInfoHeadNumber = peerInfo?.HeadNumber ?? 0;

        // TODO: try find better approach
        // Trick to enforce block number comparison if peer doesn't support TD
        if (peerInfo is { SupportsTotalDifficulty: false })
        {
            headerDifficulty = (UInt256)headerNumber;
            peerDifficulty = (UInt256)peerInfo.HeadNumber;
        }

        return peerStrategy.Compare((headerDifficulty, headerNumber), (peerDifficulty, peerInfoHeadNumber));
    }

    public static int Compare(this IBetterPeerStrategy peerStrategy, (UInt256 TotalDifficulty, long Number) value, ISyncPeer? peerInfo)
    {
        UInt256 peerDifficulty = peerInfo?.TotalDifficulty ?? UInt256.Zero;
        long peerInfoHeadNumber = peerInfo?.HeadNumber ?? 0;

        // Trick to enforce block number comparison if peer doesn't support TD
        if (peerInfo is { SupportsTotalDifficulty: false })
        {
            value = ((UInt256)value.Number, value.Number);
            peerDifficulty = (UInt256)peerInfo.HeadNumber;
        }

        return peerStrategy.Compare(value, (peerDifficulty, peerInfoHeadNumber));
    }
}
