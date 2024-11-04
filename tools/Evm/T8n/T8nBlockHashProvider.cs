// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.T8n.Errors;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Evm.t8n;

public class T8nBlockHashProvider : IBlockhashProvider
{
    private readonly Dictionary<long, Hash256?> _blockHashes = new();
    private static readonly int _maxDepth = 256;

    public Hash256? GetBlockhash(BlockHeader currentBlock, in long number)
    {
        long current = currentBlock.Number;
        if (number >= current || number < current - Math.Min(current, _maxDepth))
        {
            return null;
        }

        return _blockHashes.GetValueOrDefault(number, null) ??
               throw new T8nException($"BlockHash for block {number} not provided",
                   T8nErrorCodes.ErrorMissingBlockhash);
    }

    public void Insert(Hash256 blockHash, long number)
    {
        _blockHashes[number] = blockHash;
    }
}
