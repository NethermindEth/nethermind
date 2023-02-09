// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Enr;

/// <summary>
/// Represents the Ethereum Fork ID (hash of a list of fork block numbers and the next fork block number).
/// </summary>
public struct ForkId
{
    public ForkId(byte[] forkHash, long nextBlock)
    {
        ForkHash = forkHash;
        NextBlock = nextBlock;
    }

    /// <summary>
    /// Hash of a list of the past fork block numbers.
    /// </summary>
    public byte[] ForkHash { get; set; }

    /// <summary>
    /// Block number of the next known fork (or 0 if no fork is expected).
    /// </summary>
    public long NextBlock { get; set; }
}
