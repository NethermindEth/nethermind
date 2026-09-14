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
        /// conflict-missing on every call. 8192 references = 64 KB per armed block, allocated lazily on the
        /// first armed resolution so the forks that never reach this path pay nothing.</summary>
        private const int StateHashCacheSize = 8192;

        // The block the memo is armed for, with the table it guards, published as one reference so the gate
        // and the entries it admits can never be observed out of step. See TryGetCachedBlockHashFromState.
        private ArmedBlock? _armed;

        // The header whose EIP-2935 ring-buffer write has been observed. Until that write lands the slot
        // the block is about to overwrite still holds the occupant from a ring-size earlier, so nothing
        // may be memoized against it. See RingBufferWritten.
        private BlockHeader? _ringBufferWrittenFor;

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
        /// Two independent checks guard a hit. The <b>arming gate</b>, keyed (number, hash) by
        /// <see cref="ArmedBlock"/>, decides whether the caller is a prefetching block processor at all: only
        /// <see cref="Prefetch"/> arms it, so an env that never prefetches (the pooled RPC envs, which may
        /// execute under state overrides on their own scoped provider) reads the store directly — the same
        /// block can back different states there. The <b>per-entry check</b> is separate and reference-based:
        /// a stored slot is served only to the exact header instance it was resolved against, which keeps the
        /// prewarmer's suggested-header run and a sequential-retry re-run from reading each other's entries.
        /// <para>
        /// Why the values cannot be stale: the ring buffer is written once per block by the system call
        /// before any transaction runs, and <see cref="RingBufferWritten"/> holds the memo shut until that
        /// write is observed — so the EIP-4788 beacon-root call, which is EVM and runs before it, cannot
        /// memoize the slot's previous occupant. After it, the canonical EIP-2935 contract only stores for
        /// SYSTEM_ADDRESS, so no transaction can write to it — a chain pointing Eip2935ContractAddress at
        /// writable code would invalidate this. Cached values come from state rather than from the block
        /// tree, which matters at the fork boundary where the buffer is still filling and the two disagree.</para>
        /// </remarks>
        private bool TryGetCachedBlockHashFromState(BlockHeader currentBlock, ulong number, IReleaseSpec spec, out ReadOnlySpan<byte> hash)
        {
            ArmedBlock? armed = Volatile.Read(ref _armed);
            if (armed is null || armed.Number != currentBlock.Number || armed.Hash != currentBlock.Hash
                || !RingBufferWritten(currentBlock, spec))
            {
                // Unarmed caller (an RPC env that never prefetches, possibly executing under state
                // overrides), or the block's own ring-buffer write has not landed yet: read the store
                // directly, costing one Hash256 per call.
                Hash256? unmemoized = _blockhashStore.GetBlockHashFromState(currentBlock, number, spec);
                hash = unmemoized is null ? default : unmemoized.Bytes;
                return unmemoized is not null;
            }

            ref CachedBlockhash? slot = ref armed.Cache[(int)(number & (StateHashCacheSize - 1))];

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

        /// <summary>Whether this block has already written its parent hash into the ring buffer.</summary>
        /// <remarks>
        /// EIP-2935 stores the parent hash at <c>(number - 1) % ring size</c> at the start of the block, but
        /// the EIP-4788 beacon-root call runs before that — on this same header, and it is EVM. Were it to
        /// execute BLOCKHASH for the parent under EIP-7709, the memo would capture that slot's previous
        /// occupant (the block a ring size earlier, which is still servable) and every transaction in the
        /// block would then read that instead of the parent. Observing the write rather than assuming no EVM
        /// precedes it removes the dependency; it costs one state read per block, of the slot the parent
        /// lookup reads anyway.
        /// </remarks>
        private bool RingBufferWritten(BlockHeader currentBlock, IReleaseSpec spec)
        {
            if (ReferenceEquals(Volatile.Read(ref _ringBufferWrittenFor), currentBlock)) return true;
            if (currentBlock.Number == 0 || currentBlock.ParentHash is null) return false;

            ValueHash256 parent = default;
            if (!_blockhashStore.TryGetBlockHashFromState(currentBlock, (ulong)currentBlock.Number - 1, spec, parent.BytesAsSpan)
                || parent != currentBlock.ParentHash.ValueHash256)
            {
                return false;
            }

            Volatile.Write(ref _ringBufferWrittenFor, currentBlock);
            return true;
        }

        /// <summary>The memo table for one armed block, together with the identity it may be served to.</summary>
        /// <remarks>The identity is (number, hash) rather than the header reference because BlockProcessor
        /// executes a <c>CloneForProcessing</c> of the suggested header — same number and hash, a different
        /// instance — so a reference gate would never hit in real processing. Arming publishes a new instance,
        /// which is what bounds an entry's life to its block: a late writer from the previous block writes
        /// into a table nothing reads any more.</remarks>
        private sealed class ArmedBlock(ulong number, Hash256? hash)
        {
            private CachedBlockhash?[]? _cache;

            public ulong Number { get; } = number;
            public Hash256? Hash { get; } = hash;

            public CachedBlockhash?[] Cache => _cache ?? Initialize();

            private CachedBlockhash?[] Initialize()
            {
                // Racing initialisers may each publish an empty table; the worst case is a dropped memo
                // entry, never a wrong answer.
                CachedBlockhash?[] fresh = new CachedBlockhash?[StateHashCacheSize];
                return Interlocked.CompareExchange(ref _cache, fresh, null) ?? fresh;
            }
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
            // Arm the state memo for this block, which drops the previous block's entries with the table
            // they lived in.
            Volatile.Write(ref _ringBufferWrittenFor, null);
            Volatile.Write(ref _armed, new ArmedBlock(currentBlock.Number, currentBlock.Hash));

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
