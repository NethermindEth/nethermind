// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Blockchain
{
    public class BlockhashProvider(
        IBlockhashCache blockhashCache,
        IWorldState worldState,
        ILogManager? logManager)
        : IBlockhashProvider
    {
        public const int MaxDepth = 256;
        private readonly IBlockhashStore _blockhashStore = new BlockhashStore(worldState);
        private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        private Hash256[]? _hashes;

        public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec)
        {
            if (number < 0)
            {
                return ReturnOutOfBounds(currentBlock, number);
            }

            if (spec.IsBlockHashInStateAvailable)
            {
                return _blockhashStore.GetBlockHashFromState(currentBlock, number, spec);
            }

            long depth = currentBlock.Number - number;
            Hash256[]? hashes = _hashes;

            return depth switch
            {
                <= 0 => ReturnOutOfBounds(currentBlock, number),
                1 => currentBlock.ParentHash,
                > MaxDepth => ReturnOutOfBounds(currentBlock, number),
                _ => hashes is not null
                    ? hashes[depth - 1]
                    : blockhashCache.GetHash(currentBlock, (int)depth)
                      ?? throw new InvalidDataException("Hash cannot be found when executing BLOCKHASH operation")
            };
        }

        private Hash256? ReturnOutOfBounds(BlockHeader currentBlock, long number)
        {
            if (_logger.IsTrace) _logger.Trace($"BLOCKHASH opcode returning null for {currentBlock.Number} -> {number}");
            return null;
        }

        public async Task Prefetch(BlockHeader currentBlock, CancellationToken token)
        {
            _hashes = null;
            Hash256[]? hashes = await blockhashCache.Prefetch(currentBlock, token);

            // This leverages that branch processing is single threaded
            // If the cancellation was requested it means block processing finished before prefetching is done
            // This means we don't want to set hashes, as next block might already be prefetching
            // This allows us to avoid await on Prefetch in BranchProcessor
            lock (_blockhashStore)
            {
                if (!token.IsCancellationRequested)
                {
                    _hashes = hashes;
                }
            }
        }
    }
}
