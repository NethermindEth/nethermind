// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

public class StatelessBlockhashCache(Dictionary<Hash256, BlockHeader> headersByHash, Dictionary<long, BlockHeader> headersByNumber) : IBlockhashCache
{
    public Hash256? GetHash(BlockHeader headBlock, int depth) => headersByHash[headBlock.Hash!].Hash;

    public Task<Hash256[]?> Prefetch(BlockHeader blockHeader, CancellationToken cancellationToken)
    {
        const int length = BlockhashCache.MaxDepth + 1;
        Hash256[] result = new Hash256[length];
        result[0] = blockHeader.Hash;
        for (int i = 1; i < length; i++)
        {
            if (headersByNumber.TryGetValue(i, out BlockHeader header))
            {
                result[i] = header.Hash;
            }
        }

        return Task.FromResult(result);
    }
}
