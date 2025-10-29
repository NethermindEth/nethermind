// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Autofac.Features.GeneratedFactories;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public class CommitSetQueue
{
    private SortedSet<BlockCommitSet> _queue = new();

    public int Count
    {
        get
        {
            lock (_queue)
            {
                return _queue.Count;
            }
        }
    }

    public bool IsEmpty => Count == 0;
    public long? MinBlockNumber
    {
        get
        {
            lock (_queue) return _queue.Min?.BlockNumber;
        }
    }

    public long? MaxBlockNumber
    {
        get
        {
            lock (_queue) return _queue.Max?.BlockNumber;
        }
    }

    public void Enqueue(BlockCommitSet set)
    {
        lock (_queue) _queue.Add(set);
    }

    public bool TryPeek(out BlockCommitSet? blockCommitSet)
    {
        lock (_queue)
        {
            if (_queue.Count == 0)
            {
                blockCommitSet = null;
                return false;
            }

            blockCommitSet = _queue.Min;
            return true;
        }
    }

    public bool TryDequeue(out BlockCommitSet? blockCommitSet)
    {
        lock (_queue)
        {
            if (_queue.Count == 0)
            {
                blockCommitSet = null;
                return false;
            }

            blockCommitSet = _queue.Min;
            _queue.Remove(blockCommitSet);
            return true;
        }
    }

    public ArrayPoolListRef<BlockCommitSet> GetCommitSetsAtBlockNumber(long blockNumber)
    {
        lock (_queue)
        {
            BlockCommitSet lowerBound = new BlockCommitSet(blockNumber);
            lowerBound.Seal(new TrieNode(NodeType.Unknown, Hash256.Zero));
            BlockCommitSet upperBound = new BlockCommitSet(blockNumber);
            upperBound.Seal(new TrieNode(NodeType.Unknown, Keccak.MaxValue));

            var result = new ArrayPoolListRef<BlockCommitSet>();
            result.AddRange(_queue.GetViewBetween(lowerBound, upperBound));
            return result;
        }
    }

    public ArrayPoolListRef<BlockCommitSet> GetAndDequeueCommitSetsBeforeOrAt(long blockNumber)
    {
        lock (_queue)
        {
            var result = new ArrayPoolListRef<BlockCommitSet>();
            while (_queue.Count > 0)
            {
                BlockCommitSet min = _queue.Min;
                if (min.BlockNumber > blockNumber) break;
                result.Add(min);
                _queue.Remove(min);
            }

            return result;
        }
    }

    public void Remove(BlockCommitSet blockCommitSet)
    {
        lock (_queue) _queue.Remove(blockCommitSet);
    }
}
