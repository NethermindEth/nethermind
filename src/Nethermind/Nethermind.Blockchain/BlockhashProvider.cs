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

        /// <summary>Covers the whole EIP-2935 window (the next power of two above
        /// <see cref="Eip2935Constants.RingBufferSize"/> = 8191), so a contract sweeping BLOCKHASH across
        /// the full servable range takes at most one miss per distinct number per block instead of
        /// conflict-missing on every call. 8192 references = 64 KB per provider, allocated lazily on the
        /// first armed resolution so the forks that never reach this path pay nothing.</summary>
        private const int StateHashCacheSize = 8192;
        private CachedBlockhash?[]? _stateHashCache;

        // The memo is armed by Prefetch, which branch processing calls once per block: only the armed
        // block is served, and arming clears the table. A pooled RPC env that applies state overrides
        // never prefetches, so on its own scoped provider it can neither populate nor read the memo. The
        // arming gate is keyed (number, hash), not by reference, because BlockProcessor executes a
        // CloneForProcessing of the suggested header — same number and hash, a different instance — so a
        // reference gate would never hit in real processing. The per-entry check below is the separate,
        // reference-based half: it decides whether a stored slot belongs to this exact header instance.
        private ulong _armedNumber = ulong.MaxValue;
        private Hash256? _armedHash;

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

        /// <inheritdoc/>
        public bool TryGetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec, out ReadOnlySpan<byte> hash)
        {
            if (spec.IsBlockHashInStateAvailable)
            {
                return TryGetCachedBlockHashFromState(currentBlock, number, spec, out hash);
            }

            Hash256? blockHash = GetBlockhash(currentBlock, number, spec);
            hash = blockHash is null ? default : blockHash.Bytes;
            return blockHash is not null;
        }

        /// <summary>Serves EIP-2935 lookups from a per-block memo of what state already returned.</summary>
        /// <remarks>
        /// Two independent checks guard a hit. The <b>arming gate</b>, keyed (number, hash), decides whether
        /// the caller is a prefetching block processor at all: only <see cref="Prefetch"/> arms it, so an env
        /// that never prefetches (the pooled RPC envs, which may execute under state overrides on their own
        /// scoped provider) reads the store directly — the same block can back different states there. It is
        /// keyed by value, not reference, so it survives the <c>CloneForProcessing</c> that BlockProcessor
        /// executes with. The <b>per-entry check</b> is separate and reference-based: a stored slot is served
        /// only to the exact header instance it was resolved against, which keeps the prewarmer's
        /// suggested-header run and a sequential-retry re-run from reading each other's entries.
        /// <para>
        /// Why the values cannot be stale: the ring buffer is written once per block by the system call
        /// before any transaction runs, and the canonical EIP-2935 contract only stores for SYSTEM_ADDRESS,
        /// so no transaction can write to it — a chain pointing Eip2935ContractAddress at writable code would
        /// invalidate this. Cached values come from state rather than from the block tree, which matters at
        /// the fork boundary where the buffer is still filling and the two disagree.</para>
        /// </remarks>
        private bool TryGetCachedBlockHashFromState(BlockHeader currentBlock, ulong number, IReleaseSpec spec, out ReadOnlySpan<byte> hash)
        {
            if (currentBlock.Number != Volatile.Read(ref _armedNumber) || currentBlock.Hash != Volatile.Read(ref _armedHash))
            {
                // Unarmed caller (an RPC env that never prefetches, possibly executing under state
                // overrides): read the store directly, costing one Hash256 per call.
                Hash256? unmemoized = _blockhashStore.GetBlockHashFromState(currentBlock, number, spec);
                hash = unmemoized is null ? default : unmemoized.Bytes;
                return unmemoized is not null;
            }

            CachedBlockhash?[] cache = _stateHashCache ?? InitializeStateHashCache();
            ref CachedBlockhash? slot = ref cache[(int)(number & (StateHashCacheSize - 1))];

            CachedBlockhash? entry = Volatile.Read(ref slot);
            if (entry is not null && entry.Number == number && ReferenceEquals(entry.Header, currentBlock))
            {
                hash = entry.Bytes;
                return true;
            }

            ValueHash256 padded = default;
            if (!_blockhashStore.TryGetBlockHashFromState(currentBlock, number, spec, padded.BytesAsSpan))
            {
                hash = default;
                return false;
            }

            entry = new CachedBlockhash(currentBlock, number, padded);
            Volatile.Write(ref slot, entry);
            hash = entry.Bytes;
            return true;
        }

        private CachedBlockhash?[] InitializeStateHashCache()
        {
            // Racing initialisers may each publish an empty table; the worst case is a dropped memo
            // entry, never a wrong answer.
            CachedBlockhash?[] fresh = new CachedBlockhash?[StateHashCacheSize];
            return Interlocked.CompareExchange(ref _stateHashCache, fresh, null) ?? fresh;
        }

        /// <summary>One resolved ring-buffer entry, immutable so that a racing reader sees all of it or none.</summary>
        /// <remarks>Transactions may execute in parallel, and a torn read pairing one block number with
        /// another's bytes would be a consensus fault, so the entry is published as a single reference.</remarks>
        private sealed class CachedBlockhash(BlockHeader header, ulong number, ValueHash256 hash)
        {
            private readonly ValueHash256 _hash = hash;

            public BlockHeader Header { get; } = header;
            public ulong Number { get; } = number;

            public ReadOnlySpan<byte> Bytes => _hash.Bytes;
        }

        private Hash256? ReturnOutOfBounds(BlockHeader currentBlock, ulong number)
        {
            if (_logger.IsTrace) _logger.Trace($"BLOCKHASH opcode returning null for {currentBlock.Number} -> {number}");
            return null;
        }

        public async Task Prefetch(BlockHeader currentBlock, CancellationToken token)
        {
            // Arm the state memo for this block and drop the previous block's entries. A late writer from
            // the previous block can still publish after the clear, but its entry carries the old header
            // and misses the reference check, so the stale window closes itself.
            CachedBlockhash?[]? stateCache = Volatile.Read(ref _stateHashCache);
            if (stateCache is not null) Array.Clear(stateCache);
            Volatile.Write(ref _armedHash, currentBlock.Hash);
            Volatile.Write(ref _armedNumber, currentBlock.Number);

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
