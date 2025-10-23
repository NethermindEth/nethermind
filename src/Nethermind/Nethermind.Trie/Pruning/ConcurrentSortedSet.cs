// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using Autofac.Features.GeneratedFactories;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public class CommitSetQueue : IEnumerable<BlockCommitSet>
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
    public long MinBlockNumber => _queue.Min?.BlockNumber;
    public long MaxBlockNumber => _queue.Max?.BlockNumber;

    public void Enqueue(BlockCommitSet set)
    {
        lock (_queue)
        {
            _queue.Add(set);
        }
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

    public bool TryDequeue(out BlockCommitSet blockCommitSet)
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

    // TODO: Not concurrent safe
    public IEnumerator<BlockCommitSet> GetEnumerator()
    {
        return _queue.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public bool TryFindCommit(long finalizedHeaderNumber, Hash256 finalizedHeaderStateRoot, out BlockCommitSet blockCommitSet)
    {
        throw new System.NotImplementedException();
    }
}
