// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
            // GetViewBetween only needs (blockNumber, stateRoot) for IComparable<BlockCommitSet>;
            // SealAsBound supplies the comparison hash directly so we do not have to allocate an
            // Unknown TrieNode placeholder purely to carry Hash256.Zero / Keccak.MaxValue.
            BlockCommitSet lowerBound = new(blockNumber);
            lowerBound.SealAsBound(Hash256.Zero);
            BlockCommitSet upperBound = new(blockNumber);
            upperBound.SealAsBound(Keccak.MaxValue);

            ArrayPoolListRef<BlockCommitSet> result = new();
            result.AddRange(_queue.GetViewBetween(lowerBound, upperBound));
            return result;
        }
    }

    public ArrayPoolListRef<BlockCommitSet> GetAndDequeueCommitSetsBeforeOrAt(long blockNumber)
    {
        lock (_queue)
        {
            ArrayPoolListRef<BlockCommitSet> result = new();
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
