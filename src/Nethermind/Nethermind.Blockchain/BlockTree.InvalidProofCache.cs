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
            if ((block.StarkProof?.Length ?? 0) > Eip8288Constants.MaxAggregateProofBytes ||
                block.InclusionLists is { Count: > Eip7805Constants.InclusionListCommitteeSize } || HasOversizedListProof(block))
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

        public static long ProofBytes(Block block)
        {
            long bytes = block.StarkProof?.Length ?? 0;
            foreach (CommitteeInclusionList list in block.InclusionLists ?? []) bytes += list.Proof?.StarkProof.Length ?? 0;
            return bytes;
        }

        private static bool HasOversizedListProof(Block block)
        {
            foreach (CommitteeInclusionList list in block.InclusionLists ?? [])
                if ((list.Proof?.StarkProof.Length ?? 0) > Eip8288Constants.MaxAggregateProofBytes) return true;
            return false;
        }

        private static CommitteeInclusionList[]? Copy(IReadOnlyList<CommitteeInclusionList>? lists)
        {
            if (lists is null) return null;
            CommitteeInclusionList[] copy = new CommitteeInclusionList[lists.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = new(lists[i].CommitteeIndex, lists[i].Transactions, Copy(lists[i].Proof));
            return copy;
        }

        private static RecursiveStark? Copy(RecursiveStark? proof) => proof is null ? null :
            new RecursiveStark((byte[])proof.StarkProof.Clone(), proof.BlockDepsHash);

        private static Block Snapshot(Block block)
        {
            BlockHeader header = block.Header.Clone();
            // Keep full invalid-block metadata; only mutable proof buffers require private ownership.
            BlockBody body = new(block.Transactions, block.Uncles, block.Withdrawals, block.StarkProof is { } proof ? (byte[])proof.Clone() : null);
            return new Block(header, body, block.BlockAccessList)
            {
                GeneratedBlockAccessList = block.GeneratedBlockAccessList,
                ExecutionRequests = block.ExecutionRequests,
                InclusionListTransactions = block.InclusionListTransactions,
                InclusionLists = Copy(block.InclusionLists),
                IsInclusionListSatisfied = block.IsInclusionListSatisfied,
                AccountChanges = block.AccountChanges,
                EncodedSize = block.EncodedSize,
                EncodedBlockAccessList = block.EncodedBlockAccessList,
                EncodedTransactions = block.EncodedTransactions
            };
        }
    }
}
