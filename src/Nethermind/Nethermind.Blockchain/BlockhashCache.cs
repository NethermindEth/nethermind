// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Blockchain;

public class BlockhashCache(IHeaderFinder headerFinder, ILogManager logManager) : IDisposable, IBlockhashCache
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ConcurrentDictionary<Hash256AsKey, CacheNode> _blocks = new();
    const int MaxDepth = 256;

    public Hash256? GetHash(BlockHeader headBlock, int depth) =>
        depth == 0 ? headBlock.Hash : Load(headBlock, depth)?.Hash;

    private CacheNode? Load(BlockHeader blockHeader, int depth)
    {
        depth = Math.Min(depth, MaxDepth);
        using ArrayPoolListRef<(CacheNode Node, bool NeedToAdd)> blocks = new(depth + 1);
        Hash256 currentHash = blockHeader.Hash!;
        CacheNode currentNode = null;
        bool needToAddAny = false;
        for (int i = 0; i <= depth; i++)
        {
            bool needToAdd = false;
            if (currentNode is null)
            {
                if (!_blocks.TryGetValue(currentHash, out currentNode))
                {
                    BlockHeader? currentHeader = i == 0 ? blockHeader : headerFinder.Get(currentHash, blockHeader.Number - i);
                    if (currentHeader is null)
                    {
                        break;
                    }

                    currentNode = new CacheNode(currentHeader);
                    needToAdd = true;
                    needToAddAny = true;
                }
            }

            blocks.Add((currentNode, needToAdd));
            currentHash = currentNode!.ParentHash;
            currentNode = currentNode!.Parent;
        }

        if (needToAddAny)
        {
            (CacheNode? Node, bool NeedToAdd) parentNode = blocks[^1];
            for (int i = blocks.Count - 2; i >= 0; i--)
            {
                if (parentNode.NeedToAdd)
                {
                    _blocks.TryAdd(parentNode.Node.Hash, parentNode.Node);
                }

                (CacheNode Node, bool NeedToAdd) current = blocks[i];
                current.Node.Parent = parentNode.Node;
                parentNode = current;
            }

            if (parentNode.NeedToAdd)
            {
                _blocks.TryAdd(parentNode.Node.Hash, parentNode.Node);
            }
        }

        return blocks.Count > depth ? blocks[depth].Node : null;
    }

    public Task Prefetch(BlockHeader blockHeader)
    {
        return Task.Run(() =>
        {
            try
            {
                Load(blockHeader, MaxDepth);
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn) _logger.Warn($"Background fetch failed for block {blockHeader.Number}: {ex.Message}");
            }
        });
    }

    public int PruneBefore(long blockNumber)
    {
        int removed = 0;

        foreach (KeyValuePair<Hash256AsKey, CacheNode> kvp in _blocks)
        {
            if (kvp.Value.Parent?.Number < blockNumber)
            {
                kvp.Value.Parent = null;
            }

            if (kvp.Value.Number < blockNumber)
            {
                _blocks.TryRemove(kvp.Key, out _);
                removed++;
            }
        }

        return removed;
    }

    public bool Contains(Hash256 blockHash) => _blocks.ContainsKey(blockHash);

    public void Clear()
    {
        _blocks.Clear();
    }

    public void Dispose()
    {
        Clear();
    }

    /// <summary>
    /// Represents a cached block node in a linked-list structure
    /// </summary>
    private class CacheNode(BlockHeader blockHeader, CacheNode? parent = null)
    {
        public Hash256 Hash { get; } = blockHeader.Hash!;
        public long Number { get; } = blockHeader.Number;
        public Hash256 ParentHash { get; } = blockHeader.ParentHash!;
        public CacheNode? Parent { get; set;  } = parent;
    }
}
