// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

public class StatelessBlockhashCache(Dictionary<long, BlockHeader> headersByNumber) : IBlockhashCache
{
    public Hash256? GetHash(BlockHeader headBlock, int depth) =>
        depth == 0
            ? headBlock.Hash
            : headersByNumber.TryGetValue(headBlock.Number - depth, out BlockHeader? header)
                ? header?.Hash
                : null;

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
