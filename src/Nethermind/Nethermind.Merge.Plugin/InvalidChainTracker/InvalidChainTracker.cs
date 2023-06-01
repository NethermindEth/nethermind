// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
public class InvalidChainTracker : IInvalidChainTracker
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IBlockFinder _blockFinder;
    private readonly IBlockCacheService _blockCacheService;
    private readonly ILogger _logger;
    private readonly LruCache<ValueKeccak, Node> _tree;

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
        _tree = new(1024, nameof(InvalidChainTracker));
        _logger = logManager.GetClassLogger<InvalidChainTracker>();
        _blockCacheService = blockCacheService;
    }

    public void SetupBlockchainProcessorInterceptor(IBlockchainProcessor blockchainProcessor)
    {
        blockchainProcessor.InvalidBlock += OnBlockchainProcessorInvalidBlock;
        _disposables.Add(() =>
        {
            blockchainProcessor.InvalidBlock -= OnBlockchainProcessorInvalidBlock;
        });
    }

    private void OnBlockchainProcessorInvalidBlock(object? sender, IBlockchainProcessor.InvalidBlockEventArgs args) => OnInvalidBlock(args.InvalidBlock.Hash!, args.InvalidBlock.ParentHash);

    public void SetChildParent(Keccak child, Keccak parent)
    {
        Node parentNode = GetNode(parent);
        bool needPropagate;
        lock (parentNode)
        {
            parentNode.Children.Add(child);
            needPropagate = parentNode.LastValidHash is not null;
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
        HashSet<Node> visited = new() { node };

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

    private BlockHeader? TryGetBlockHeaderIncludingInvalid(Keccak hash)
    {
        if (_blockCacheService.BlockCache.TryGetValue(hash, out Block? block))
        {
            return block.Header;
        }

        return _blockFinder.FindHeader(hash, BlockTreeLookupOptions.AllowInvalid | BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
    }

    public void OnInvalidBlock(Keccak failedBlock, Keccak? parent)
    {
        if (_logger.IsDebug) _logger.Debug($"OnInvalidBlock: {failedBlock} {parent}");

        // TODO: This port can now be removed? We should never get null here?
        if (parent is null)
        {
            BlockHeader? failedBlockHeader = TryGetBlockHeaderIncludingInvalid(failedBlock);
            if (failedBlockHeader is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Unable to resolve block to determine parent. Block {failedBlock}");
                return;
            }

            parent = failedBlockHeader.ParentHash!;
        }

        Keccak effectiveParent = parent;
        BlockHeader? parentHeader = TryGetBlockHeaderIncludingInvalid(parent);
        if (parentHeader is not null)
        {
            if (!_poSSwitcher.IsPostMerge(parentHeader))
            {
                effectiveParent = Keccak.Zero;
            }
        }
        else
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to resolve parent to determine if it is post merge. Assuming post merge. Block {parent}");
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
            if (node.LastValidHash is not null)
            {
                lastValidHash = node.LastValidHash;
            }

            return node.LastValidHash is not null;
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
