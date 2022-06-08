//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

/// <summary>
/// Tracks if a given hash is on a known invalid chain, as one if it's ancestor have been reported to be invalid.
/// 
/// </summary>
public class InvalidChainTracker: IInvalidChainTracker
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IBlockFinder _blockFinder;
    private readonly IBlockCacheService _blockCacheService;
    private readonly ILogger _logger;
    private readonly LruCache<Keccak, Node> _tree;
    
    // CompositeDisposable only available on System.Reactive. So this will do for now. 
    private readonly List<Action> _disposables = new();

    public InvalidChainTracker(
        IPoSSwitcher poSSwitcher, 
        IBlockFinder blockFinder,
        IBlockCacheService blockCacheService,
        ILogManager logManager)
    {
        _poSSwitcher = poSSwitcher;
        _blockFinder = blockFinder;
        _tree = new LruCache<Keccak, Node>(1024, nameof(InvalidChainTracker));
        _logger = logManager.GetClassLogger<InvalidChainTracker>();
        _blockCacheService = blockCacheService;
    }

    public void SetupBlockchainProcessorInterceptor(IBlockchainProcessor blockchainProcessor)
    {
        blockchainProcessor.OnInvalidBlock += BlockchainProcessorOnOnInvalidBlock;
        _disposables.Add(() =>
        {
            blockchainProcessor.OnInvalidBlock -= BlockchainProcessorOnOnInvalidBlock;
        });
    }

    private void BlockchainProcessorOnOnInvalidBlock(object? sender, IBlockchainProcessor.OnInvalidBlockArg onInvalidBlockArg)
    {
        OnInvalidBlock(onInvalidBlockArg.InvalidBlockHash, null);
    }

    public void SetChildParent(Keccak child, Keccak parent)
    {
        Node parentNode = GetNode(parent);
        bool needPropagate = false;
        lock (parentNode)
        {
            parentNode.Children.Add(child);
            needPropagate = parentNode.LastValidHash != null;
        }
        
        if (needPropagate)
        {
            PropagateLastValidHash(parentNode);
        }
    }

    private Node GetNode(Keccak hash)
    {
        if (!_tree.TryGet(hash, out Node node))
        {
            node = new Node();
            _tree.Set(hash, node);
        }

        return node;
    }

    private void PropagateLastValidHash(Node node)
    {
        Queue<Node> bfsQue = new();
        bfsQue.Enqueue(node);
        HashSet<Node> visited = new();
        visited.Add(node);

        while (bfsQue.Count > 0)
        {
            Node current = bfsQue.Dequeue();
            lock (current)
            {
                foreach (Keccak nodeChild in current.Children)
                {
                    Node childNode = GetNode(nodeChild);
                    if (childNode.LastValidHash != current.LastValidHash)
                    {
                        childNode.LastValidHash = current.LastValidHash;
                        if (!visited.Contains(childNode))
                        {
                            visited.Add(childNode);
                            bfsQue.Enqueue(childNode);
                        }
                    }
                }
            }
        }
        
    }

    private BlockHeader? TryGetBlockHeader(Keccak hash)
    {
        if (_blockCacheService.BlockCache.TryGetValue(hash, out Block block))
        {
            return block.Header;
        }

        return _blockFinder.FindHeader(hash);
    }

    public void OnInvalidBlock(Keccak failedBlock, Keccak? parent)
    {
        if (parent == null)
        {
            BlockHeader? failedBlockHeader = TryGetBlockHeader(failedBlock);
            if (failedBlockHeader == null)
            {
                if(_logger.IsWarn) _logger.Warn($"Unable to resolve block to determine parent. Block {failedBlock}");
                return;
            }

            parent = failedBlockHeader.ParentHash;
        }
        
        Keccak effectiveParent = parent;
        BlockHeader? parentHeader = TryGetBlockHeader(parent);
        if (parentHeader != null)
        {
            if (!_poSSwitcher.IsPostMerge(parentHeader))
            {
                effectiveParent = Keccak.Zero;
            }
        }
        else
        {
            if(_logger.IsTrace) _logger.Trace($"Unable to resolve parent to determine if it is post merge. Assuming post merge. Block {parent}");
        }

        Node failedBlockNode = GetNode(failedBlock);
        lock (failedBlockNode)
        {
            failedBlockNode.LastValidHash = effectiveParent;
        }
        PropagateLastValidHash(failedBlockNode);
    }

    public bool IsOnKnownInvalidChain(Keccak blockHash, out Keccak? lastValidHash)
    {
        lastValidHash = null;
        Node node = GetNode(blockHash);
        lock (node)
        {
            if (node.LastValidHash != null)
            {
                lastValidHash = node.LastValidHash;
            }

            return node.LastValidHash != null;
        }
    }

    class Node
    {
        public HashSet<Keccak> Children { get; } = new();
        public Keccak? LastValidHash { get; set; }
    }

    public void Dispose()
    {
        foreach (Action action in _disposables)
        {
            action.Invoke();
        }
    }
}
