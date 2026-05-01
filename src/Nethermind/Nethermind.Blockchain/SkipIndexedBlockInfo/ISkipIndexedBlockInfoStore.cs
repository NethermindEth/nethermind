// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Blockchain.SkipIndexedBlockInfo;

public interface ISkipIndexedBlockInfoStore
{
    UInt256? GetTotalDifficulty(long blockNumber, in ValueHash256 blockHash);
    ValueHash256? GetAncestorAt(long blockNumber, in ValueHash256 blockHash, long ancestorBlockNumber);

    /// <summary>
    /// Convenience overload: gets TD for a block header.
    /// </summary>
    UInt256? GetTotalDifficulty(BlockHeader? header)
    {
        if (header?.Hash is null) return null;
        ValueHash256 vh = header.Hash.ValueHash256;
        return GetTotalDifficulty(header.Number, in vh);
    }
}
