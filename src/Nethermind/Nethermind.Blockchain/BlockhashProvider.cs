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
        public const ulong MaxDepth = 256;
        private readonly IBlockhashStore _blockhashStore = new BlockhashStore(worldState);
        private readonly ILogger _logger = logManager?.GetClassLogger<BlockhashProvider>() ?? throw new ArgumentNullException(nameof(logManager));
        private Hash256[]? _hashes;
        private long _prefetchVersion;

        public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec)
        {
            if (spec.IsBlockHashInStateAvailable)
            {
                return _blockhashStore.GetBlockHashFromState(currentBlock, number, spec);
            }

            ulong depth = currentBlock.Number - number;
            if (depth == 0 || depth > MaxDepth)
            {
                return ReturnOutOfBounds(currentBlock, number);
            }

            Hash256[]? hashes = Volatile.Read(ref _hashes);

            return depth switch
            {
                1UL => currentBlock.ParentHash,
                _ => hashes is not null
                    ? hashes[(int)(depth - 1)]
                    : blockhashCache.GetHash(currentBlock, depth)
                      ?? OnUnresolvedBlockhash(currentBlock, number)
            };
        }

        /// <summary>
        /// Invoked for an in-window block whose hash cannot be resolved from the cache.
        /// </summary>
        /// <remarks>
        /// During canonical processing the ancestors are always available, so this is a genuine invariant
        /// violation and throwing is the correct, fail-loud behavior. eth_simulateV1 overrides this to return
        /// null (the EVM then pushes 0 per BLOCKHASH semantics) because it is best-effort over an overlay chain.
        /// </remarks>
        protected virtual Hash256? OnUnresolvedBlockhash(BlockHeader currentBlock, ulong number) =>
            throw new InvalidDataException("Hash cannot be found when executing BLOCKHASH operation");

        private Hash256? ReturnOutOfBounds(BlockHeader currentBlock, ulong number)
        {
            if (_logger.IsTrace) _logger.Trace($"BLOCKHASH opcode returning null for {currentBlock.Number} -> {number}");
            return null;
        }

        public async Task Prefetch(BlockHeader currentBlock, CancellationToken token)
        {
            long prefetchVersion = Interlocked.Increment(ref _prefetchVersion);
            Volatile.Write(ref _hashes, null);
            Hash256[]? hashes = await blockhashCache.Prefetch(currentBlock, token);

            // This leverages that branch processing is single threaded
            // If the cancellation was requested it means block processing finished before prefetching is done
            // This means we don't want to set hashes, as next block might already be prefetching
            // This allows us to avoid await on Prefetch in BranchProcessor
            lock (_blockhashStore)
            {
                if (!token.IsCancellationRequested && prefetchVersion == Interlocked.Read(ref _prefetchVersion))
                {
                    Volatile.Write(ref _hashes, hashes);
                }
            }
        }
    }
}
