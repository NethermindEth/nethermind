// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

public partial class BlockTree
{
    internal const int MaxLargeInvalidBlocks = 8;
    internal const int MaxInvalidProofCacheBytes = 64 * 1024 * 1024;

    private Block? FindInvalidBlock(Hash256 hash) => _invalidBlocks.TryGet(hash, out Block block)
        ? block : Volatile.Read(ref _invalidProofs)?.Get(hash.ValueHash256);

    private sealed class InvalidProofCache
    {
        private readonly Lock _lock = new();
        private readonly LruKeyCache<ValueHash256> _knownInvalid = new(128, "invalid proof hashes");
        private readonly LinkedList<Block> _lru = new();
        private readonly Dictionary<ValueHash256, System.Collections.Generic.LinkedListNode<Block>> _blocks = [];
        private long _proofBytes;

        public bool Contains(ValueHash256 hash)
        {
            if (_knownInvalid.Get(hash)) return true;
            lock (_lock) return _blocks.ContainsKey(hash);
        }

        public Block? Get(ValueHash256 hash)
        {
            Block snapshot;
            lock (_lock)
            {
                if (!_blocks.TryGetValue(hash, out System.Collections.Generic.LinkedListNode<Block>? node)) return null;
                _lru.Remove(node);
                _lru.AddLast(node);
                snapshot = node.Value;
            }
            return Snapshot(snapshot);
        }

        public void Set(Block block)
        {
            ValueHash256 hash = block.Hash!.ValueHash256;
            _knownInvalid.Set(hash);
            if ((block.Header.RecursiveStark?.StarkProof.Length ?? 0) > Eip8288Constants.MaxProofBytes ||
                (block.InclusionListRecursiveStark?.StarkProof.Length ?? 0) > Eip8288Constants.MaxProofBytes ||
                (block.InclusionListProvenDependencies?.Length ?? 0) > Eip8288Constants.MaxProofDependencies * 96)
            {
                Delete(hash);
                return;
            }
            Block snapshot = Snapshot(block);
            long bytes = ProofBytes(snapshot);
            lock (_lock)
            {
                DeleteCore(hash);
                while (_blocks.Count >= MaxLargeInvalidBlocks || bytes > MaxInvalidProofCacheBytes - _proofBytes)
                    DeleteCore(_lru.First!.Value.Hash!.ValueHash256);
                _blocks.Add(hash, _lru.AddLast(snapshot));
                _proofBytes += bytes;
            }
        }

        public void Delete(ValueHash256 hash) { lock (_lock) DeleteCore(hash); }

        private void DeleteCore(ValueHash256 hash)
        {
            if (!_blocks.Remove(hash, out System.Collections.Generic.LinkedListNode<Block>? node)) return;
            _lru.Remove(node);
            _proofBytes -= ProofBytes(node.Value);
        }

        public static long ProofBytes(Block block) => (long)(block.Header.RecursiveStark?.StarkProof.Length ?? 0) +
            (block.InclusionListRecursiveStark?.StarkProof.Length ?? 0) + (block.InclusionListProvenDependencies?.Length ?? 0);

        private static RecursiveStark? Copy(RecursiveStark? proof) => proof is null ? null :
            new RecursiveStark((byte[])proof.StarkProof.Clone(), proof.BlockDepsHash);

        private static Block Snapshot(Block block)
        {
            BlockHeader header = block.Header.Clone();
            header.RecursiveStark = Copy(block.Header.RecursiveStark);
            // Keep full invalid-block metadata; only mutable proof buffers require private ownership.
            return new Block(header, block.Body, block.BlockAccessList)
            {
                GeneratedBlockAccessList = block.GeneratedBlockAccessList,
                ExecutionRequests = block.ExecutionRequests,
                InclusionListTransactions = block.InclusionListTransactions,
                InclusionListRecursiveStark = Copy(block.InclusionListRecursiveStark),
                InclusionListProvenDependencies = (byte[]?)block.InclusionListProvenDependencies?.Clone(),
                IsInclusionListSatisfied = block.IsInclusionListSatisfied,
                AccountChanges = block.AccountChanges,
                EncodedSize = block.EncodedSize,
                EncodedBlockAccessList = block.EncodedBlockAccessList,
                EncodedTransactions = block.EncodedTransactions
            };
        }
    }
}
