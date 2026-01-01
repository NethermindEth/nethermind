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
        UInt256? headerDifficulty = header?.TotalDifficulty;
        ulong headerNumber = header?.Number ?? 0;

        UInt256? peerDifficulty = peerInfo?.TotalDifficulty;
        ulong peerInfoHeadNumber = peerInfo?.HeadNumber ?? 0;

        return peerStrategy.Compare((headerDifficulty, headerNumber), (peerDifficulty, peerInfoHeadNumber));
    }

    public static int Compare(this IBetterPeerStrategy peerStrategy, (UInt256 TotalDifficulty, ulong Number) value, ISyncPeer? peerInfo)
    {
        UInt256? peerDifficulty = peerInfo?.TotalDifficulty;
        ulong peerInfoHeadNumber = peerInfo?.HeadNumber ?? 0;
        return peerStrategy.Compare(value, (peerDifficulty, peerInfoHeadNumber));
    }
}
