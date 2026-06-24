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
        private readonly ILogger _logger = logManager?.GetClassLogger<BlockhashProvider>() ?? throw new ArgumentNullException(nameof(logManager));
        private Hash256[]? _hashes;
        private long _prefetchVersion;

        public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec) =>
            TryGetBlockhash(currentBlock, number, spec, out Hash256? hash)
                ? hash
                : throw new InvalidDataException("Hash cannot be found when executing BLOCKHASH operation");

        public bool TryGetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec, out Hash256? hash)
        {
            if (number < 0)
            {
                hash = ReturnOutOfBounds(currentBlock, number);
                return true;
            }

            if (spec.IsBlockHashInStateAvailable)
            {
                hash = _blockhashStore.GetBlockHashFromState(currentBlock, number, spec);
                return true;
            }

            long depth = currentBlock.Number - number;
            Hash256[]? hashes = Volatile.Read(ref _hashes);

            switch (depth)
            {
                case <= 0 or > MaxDepth:
                    hash = ReturnOutOfBounds(currentBlock, number);
                    return true;
                case 1:
                    hash = currentBlock.ParentHash;
                    return true;
                default:
                    if (hashes is not null)
                    {
                        hash = hashes[depth - 1];
                        return true;
                    }

                    // A cache miss here is unresolvable rather than out-of-window: report it so callers
                    // can decide between throwing (canonical processing) and pushing 0 (best-effort simulate).
                    hash = blockhashCache.GetHash(currentBlock, (int)depth);
                    return hash is not null;
            }
        }

        private Hash256? ReturnOutOfBounds(BlockHeader currentBlock, long number)
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
