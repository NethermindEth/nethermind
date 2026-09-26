// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public class CommitSetQueue
{
    private SortedSet<BlockCommitSet> _queue = [];

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

    internal bool TryGetBounds(out ulong minBlockNumber, out ulong maxBlockNumber)
    {
        lock (_queue)
        {
            if (_queue.Min is not { } min || _queue.Max is not { } max)
            {
                minBlockNumber = default;
                maxBlockNumber = default;
                return false;
            }

            minBlockNumber = min.BlockNumber;
            maxBlockNumber = max.BlockNumber;
            return true;
        }
    }

    public void Enqueue(BlockCommitSet set)
    {
        lock (_queue) _queue.Add(set);
    }

    public bool TryPeek([NotNullWhen(true)] out BlockCommitSet? blockCommitSet)
    {
        lock (_queue)
        {
            blockCommitSet = _queue.Min;
            if (blockCommitSet is null)
            {
                return false;
            }

            return true;
        }
    }

    public bool TryDequeue([NotNullWhen(true)] out BlockCommitSet? blockCommitSet)
    {
        lock (_queue)
        {
            blockCommitSet = _queue.Min;
            if (blockCommitSet is null)
            {
                return false;
            }

            _queue.Remove(blockCommitSet);
            return true;
        }
    }

    public ArrayPoolListRef<BlockCommitSet> GetCommitSetsAtBlockNumber(ulong blockNumber)
    {
        lock (_queue)
        {
            BlockCommitSet lowerBound = new(blockNumber);
            lowerBound.Seal(new TrieNode(NodeType.Unknown, Hash256.Zero));
            BlockCommitSet upperBound = new(blockNumber);
            upperBound.Seal(new TrieNode(NodeType.Unknown, Keccak.MaxValue));

            ArrayPoolListRef<BlockCommitSet> result = new();
            result.AddRange(_queue.GetViewBetween(lowerBound, upperBound));
            return result;
        }
    }

    public ArrayPoolListRef<BlockCommitSet> GetAndDequeueCommitSetsBeforeOrAt(ulong blockNumber)
    {
        lock (_queue)
        {
            ArrayPoolListRef<BlockCommitSet> result = new();
            while (_queue.Min is { } min)
            {
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

    public void Clear()
    {
        lock (_queue) _queue.Clear();
    }
}
