// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Evm.T8NTool;

public class T8NBlockHashProvider : IBlockhashProvider
{
    private readonly Dictionary<long, Hash256> _blockHashes = new();
    public Hash256 GetBlockhash(BlockHeader currentBlock, in long number)
    {
        return _blockHashes[number] ?? throw new InvalidDataException($"BlockHash for block {number} not provided");
    }

    public void Insert(Hash256? blockHash, long number)
    {
        if (blockHash != null)
        {
            _blockHashes[number] = blockHash;
        }
    }
}
