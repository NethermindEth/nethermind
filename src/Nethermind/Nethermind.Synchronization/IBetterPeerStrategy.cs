// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Synchronization;

/// <summary>
/// Abstraction of checking if peers are available for syncing.
/// </summary>
public interface IBetterPeerStrategy
{
    int Compare(in (UInt256 TotalDifficulty, long Number) valueX, in (UInt256 TotalDifficulty, long Number) valueY);

    bool IsBetterThanLocalChain(in (UInt256 TotalDifficulty, long Number) bestPeerInfo, in (UInt256 TotalDifficulty, long Number) bestBlock);

    bool IsDesiredPeer(in (UInt256 TotalDifficulty, long Number) bestPeerInfo, in (UInt256 TotalDifficulty, long Number) bestHeader);

    bool IsLowerThanTerminalTotalDifficulty(UInt256 totalDifficulty) => true;
}
