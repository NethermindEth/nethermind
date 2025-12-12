// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.T8n.Errors;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Evm.T8n;

public class T8nBlockHashProvider(Dictionary<long, Hash256?> blockHashes) : IBlockhashProvider
{
    public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec? spec)
    {
        long current = currentBlock.Number;
        return number >= current || number < current - Math.Min(current, BlockhashProvider.MaxDepth)
            ? null
            : blockHashes.GetValueOrDefault(number, null) ??
              throw new T8nException($"BlockHash for block {number} not provided",
                  T8nErrorCodes.ErrorMissingBlockhash);
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
}
