// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        ILogManager? logManager,
        IUnresolvedBlockhashPolicy? unresolvedBlockhashPolicy = null)
        : IBlockhashProvider
    {
        public const ulong MaxDepth = 256;
        private readonly IBlockhashStore _blockhashStore = new BlockhashStore(worldState);
        private readonly ILogger _logger = logManager?.GetClassLogger<BlockhashProvider>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IUnresolvedBlockhashPolicy _unresolvedBlockhashPolicy = unresolvedBlockhashPolicy ?? ThrowingUnresolvedBlockhashPolicy.Instance;
        private Hash256[]? _hashes;
        private long _prefetchVersion;

        private const int StateHashCacheSize = 256;
        private readonly CachedBlockhash?[] _stateHashCache = new CachedBlockhash?[StateHashCacheSize];

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
                      ?? _unresolvedBlockhashPolicy.Resolve(currentBlock, number)
            };
        }

        public bool TryGetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec, Span<byte> destination)
        {
            if (spec.IsBlockHashInStateAvailable)
            {
                return TryGetCachedBlockHashFromState(currentBlock, number, spec, destination);
            }

            Hash256? hash = GetBlockhash(currentBlock, number, spec);
            if (hash is null) return false;

            hash.Bytes.CopyTo(destination);
            return true;
        }

        /// <summary>Serves EIP-2935 lookups from a per-block memo of what state already returned.</summary>
        /// <remarks>
        /// The ring buffer is written once per block by the system call before any transaction runs, and no
        /// transaction can write to it, so a resolved entry cannot change while the block executes. Entries
        /// carry the header they were resolved against and are matched by reference, so anything resolved for
        /// a different block simply misses rather than being served stale — there is no invalidation step to
        /// get wrong. Cached values come from state rather than from the block tree, which matters at the
        /// fork boundary where the buffer is still filling and the two disagree.
        /// </remarks>
        private bool TryGetCachedBlockHashFromState(BlockHeader currentBlock, ulong number, IReleaseSpec spec, Span<byte> destination)
        {
            ref CachedBlockhash? slot = ref _stateHashCache[(int)(number & (StateHashCacheSize - 1))];

            CachedBlockhash? entry = Volatile.Read(ref slot);
            if (entry is not null && entry.Number == number && ReferenceEquals(entry.Header, currentBlock))
            {
                entry.CopyTo(destination);
                return true;
            }

            if (!_blockhashStore.TryGetBlockHashFromState(currentBlock, number, spec, destination))
            {
                return false;
            }

            Volatile.Write(ref slot, new CachedBlockhash(currentBlock, number, destination));
            return true;
        }

        /// <summary>One resolved ring-buffer entry, immutable so that a racing reader sees all of it or none.</summary>
        /// <remarks>Transactions may execute in parallel, and a torn read pairing one block number with
        /// another's bytes would be a consensus fault, so the entry is published as a single reference.</remarks>
        private sealed class CachedBlockhash
        {
            private readonly ValueHash256 _hash;

            public CachedBlockhash(BlockHeader header, ulong number, ReadOnlySpan<byte> hash)
            {
                Header = header;
                Number = number;
                _hash = new ValueHash256(hash);
            }

            public BlockHeader Header { get; }
            public ulong Number { get; }

            public void CopyTo(Span<byte> destination) => _hash.Bytes.CopyTo(destination);
        }

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
