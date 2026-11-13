// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Logging;

namespace Nethermind.Eez.Network;

internal sealed class SequencerPeerHeadSource : IUnsafeHeadSource
{
    private readonly SequencerHeadTracker _tracker;
    private readonly IBlockTree _blockTree;
    private readonly Snapshot _snapshot;
    private readonly UnsafeHeadFollower _follower;
    private readonly Dictionary<Hash256AsKey, BlockHeader> _headers = new(UnsafeHeadFollower.MaxDepth);
    private readonly Queue<Hash256AsKey> _headerOrder = new(UnsafeHeadFollower.MaxDepth);

    public SequencerPeerHeadSource(SequencerHeadTracker tracker, SequencerPeerBlocks blocks, IBlockTree blockTree,
        IEezL2Engine engine, IDerivedHeaderRules rules, ILogManager logManager, IUnsafeBlockPreparation? preparation = null)
    {
        _tracker = tracker;
        _blockTree = blockTree;
        _snapshot = new Snapshot(blocks, blockTree);
        _follower = new UnsafeHeadFollower(_snapshot, blockTree, engine, rules, logManager, preparation, _snapshot.GetBlock);
    }

    public async Task Advance(FollowerHeads heads, CancellationToken token)
    {
        foreach (VerifiedSequencerHead candidate in _tracker.Candidates())
        {
            BlockHeader current = candidate.Block.Header;
            if (current.Number < heads.Safe.Number || current.Number == heads.Safe.Number && current.Hash != heads.Safe.Hash) continue;
            if (heads.UnsafeHeadHeld) return;
            if (current.Number > heads.Head.Number && current.Number - heads.Head.Number > UnsafeHeadFollower.MaxDepth) return;
            _snapshot.Reset(candidate);
            bool conflict = false;
            for (int depth = 0; ; depth++)
            {
                token.ThrowIfCancellationRequested();
                if (current.Number <= heads.Head.Number && _blockTree.IsMainChain(current)) break;
                if (current.Number <= heads.Safe.Number)
                {
                    conflict = current.Hash != heads.Safe.Hash;
                    break;
                }
                if (depth >= UnsafeHeadFollower.MaxDepth) return;
                _snapshot.Add(current);
                Hash256 parentHash = current.ParentHash!;
                BlockHeader? parent = _blockTree.FindHeader(parentHash) ?? _headers.GetValueOrDefault(parentHash);
                if (parent is null)
                {
                    parent = await _snapshot.ReadHeader(parentHash, token);
                    if (parent is null) return;
                    Remember(parent);
                }
                if (parent.Number + 1 != current.Number) return;
                current = parent;
            }
            if (conflict || current.Number < heads.Safe.Number) continue;
            if (heads.UnsafeHeadHeld) return;
            _snapshot.Add(current);
            _follower.ResetRefusal();
            await _follower.Advance(heads, token);
            return;
        }
    }

    private void Remember(BlockHeader header)
    {
        Hash256AsKey key = header.Hash!;
        if (_headers.ContainsKey(key)) return;
        if (_headers.Count == UnsafeHeadFollower.MaxDepth) _headers.Remove(_headerOrder.Dequeue());
        _headers.Add(key, header);
        _headerOrder.Enqueue(key);
    }

    private sealed class Snapshot(SequencerPeerBlocks blocks, IBlockTree blockTree) : IEezSequencerApi
    {
        private readonly Dictionary<ulong, BlockHeader> _byNumber = [];
        private readonly Dictionary<Hash256AsKey, BlockHeader> _byHash = [];
        private VerifiedSequencerHead _head = null!;

        internal void Reset(VerifiedSequencerHead head)
        {
            _head = head;
            _byNumber.Clear();
            _byHash.Clear();
            Add(head.Block.Header);
        }

        internal void Add(BlockHeader header)
        {
            _byNumber[header.Number] = header;
            _byHash[header.Hash!] = header;
        }

        internal Task<BlockHeader?> ReadHeader(Hash256 hash, CancellationToken token) => blocks.Header(hash, token);

        internal async Task<Block> GetBlock(Hash256 hash, CancellationToken token)
        {
            if (hash == _head.Block.Hash) return _head.Block;
            BlockHeader header = _byHash.GetValueOrDefault(hash)
                ?? throw new L1SourceIncompleteException(0, "The signed head snapshot has no requested ancestor.");
            return await blocks.Block(header, token)
                ?? throw new L1SourceIncompleteException(header.Number, "No eth peer served the requested block body.");
        }

        public Task<EezL1Block?> GetLatestBlock(CancellationToken token) => Task.FromResult<EezL1Block?>(Describe(_head.Block.Header));

        public Task<EezL1Block?> GetBlockByNumber(ulong number, CancellationToken token)
        {
            BlockHeader? header = _byNumber.GetValueOrDefault(number) ?? blockTree.FindHeader(number);
            return Task.FromResult(header is null ? null : (EezL1Block?)Describe(header));
        }

        public Task<byte[]?> GetRawBlock(Hash256 hash, CancellationToken token) => throw new NotSupportedException("The signed head snapshot supplies decoded blocks.");

        private static EezL1Block Describe(BlockHeader header) => new()
        {
            Hash = header.Hash!,
            ParentHash = header.ParentHash!,
            Number = header.Number,
            Timestamp = header.Timestamp
        };
    }
}
