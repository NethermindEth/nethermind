// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
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
public class InvalidChainTracker(
    IPoSSwitcher poSSwitcher,
    IBlockTree blockTree,
    IBlockCacheService blockCacheService,
    ILogManager logManager) : IInvalidChainTracker
{
    private readonly IPoSSwitcher _poSSwitcher = poSSwitcher;
    private readonly IBlockTree _blockTree = blockTree;
    private readonly IBlockCacheService _blockCacheService = blockCacheService;
    private readonly ILogger _logger = logManager.GetClassLogger<InvalidChainTracker>();
    private readonly LruCache<ValueHash256, Node> _tree = new(1024, nameof(InvalidChainTracker));
    private readonly LruCache<ValueHash256, Hash256> _validatedBlocks = new(1024, "validated chain blocks");

    // CompositeDisposable only available on System.Reactive. So this will do for now.
    private readonly List<Action> _disposables = [];

    public void SetupBlockchainProcessorInterceptor(IBlockProcessingQueue processingQueue, IBranchProcessor branchProcessor)
    {
        processingQueue.InvalidBlock += OnBlockchainProcessorInvalidBlock;
        branchProcessor.BlockProcessed += OnBlockProcessed;
        _disposables.Add(() =>
        {
            processingQueue.InvalidBlock -= OnBlockchainProcessorInvalidBlock;
            branchProcessor.BlockProcessed -= OnBlockProcessed;
        });
    }

    private void OnBlockchainProcessorInvalidBlock(object? sender, IBlockProcessingQueue.InvalidBlockEventArgs args) => OnInvalidBlock(args.InvalidBlock.Hash!, args.InvalidBlock.ParentHash);

    private void OnBlockProcessed(object? sender, BlockProcessedEventArgs args)
    {
        if (args.WasValidated)
        {
            Hash256 hash = args.Block.Hash!;
            // A valid prefix survives a later block's failure, which prevents the branch's WasProcessed markers.
            _validatedBlocks.Set(hash, _poSSwitcher.IsPostMerge(args.Block.Header) ? hash : Keccak.Zero);
        }
    }

    public void SetChildParent(Hash256 child, Hash256 parent)
    {
        Node parentNode = GetNode(parent);
        bool needPropagate;
        lock (parentNode)
        {
            parentNode.Children.Add(child);
            needPropagate = parentNode.IsInvalid;
        }

        if (needPropagate)
        {
            PropagateLastValidHash(parentNode);
        }
    }

    private Node GetNode(Hash256 hash) => _tree.SetOrGet(hash, 0, static (_, _) => new Node());

    private void PropagateLastValidHash(Node node)
    {
        Queue<Node> bfsQue = new();
        bfsQue.Enqueue(node);
        HashSet<Node> visited = [node];

        while (bfsQue.Count > 0)
        {
            Node current = bfsQue.Dequeue();
            lock (current)
            {
                foreach (Hash256 nodeChild in current.Children)
                {
                    Node childNode = GetNode(nodeChild);
                    // Keep only the parent's lock: nested child locks can deadlock on tolerated cycles.
                    // A reader can observe a new invalid mark before its first hash assignment and conservatively return null.
                    if (!childNode.IsInvalid || childNode.LastValidHash != current.LastValidHash)
                    {
                        childNode.IsInvalid = true;
                        childNode.LastValidHash = current.LastValidHash;
                        if (visited.Add(childNode))
                        {
                            bfsQue.Enqueue(childNode);
                        }
                    }
                }
            }
        }

    }

    private BlockHeader? TryGetBlockHeaderIncludingInvalid(Hash256 hash)
    {
        if (_blockCacheService.BlockCache.TryGetValue(hash, out Block? block))
        {
            return block.Header;
        }

        return _blockTree.FindHeader(hash, BlockTreeLookupOptions.AllowInvalid | BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The engine API's <c>latestValidHash</c> names an ancestor that is fully validated, zero for a pre-merge one,
    /// and is null when no such ancestor can be determined. It is therefore the parent only when this node executed
    /// it (including a valid prefix of a failed branch), the parent's own last valid hash when the parent is itself
    /// tracked as invalid, and unknown for a parent
    /// that is only inserted, cached or unresolved: a block rejected before execution can have an unexecuted parent,
    /// and a consensus client may mark the hash it is given valid without executing it.
    /// </remarks>
    public void OnInvalidBlock(Hash256 failedBlock, Hash256? parent)
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

        Hash256? lastValidHash = GetLastValidHash(parent);

        Node failedBlockNode = GetNode(failedBlock);
        lock (failedBlockNode)
        {
            failedBlockNode.IsInvalid = true;
            failedBlockNode.LastValidHash = lastValidHash;
        }
        PropagateLastValidHash(failedBlockNode);
    }

    private Hash256? GetLastValidHash(Hash256 parent)
    {
        if (_tree.TryGet(parent, out Node parentNode))
        {
            lock (parentNode)
            {
                if (parentNode.IsInvalid) return parentNode.LastValidHash;
            }
        }

        if (_validatedBlocks.TryGet(parent, out Hash256 validatedHash)) return validatedHash;

        BlockHeader? parentHeader = TryGetBlockHeaderIncludingInvalid(parent);
        if (parentHeader is null)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to resolve parent {parent}; the last valid hash is unknown.");
            return null;
        }

        if (!_poSSwitcher.IsPostMerge(parentHeader)) return Keccak.Zero;

        if (_blockTree.GetInfo(parentHeader.Number, parent).Info is { WasProcessed: true }) return parent;

        if (_logger.IsTrace) _logger.Trace($"Parent {parent} was not executed; the last valid hash is unknown.");
        return null;
    }

    public bool IsOnKnownInvalidChain(Hash256 blockHash, out Hash256? lastValidHash)
    {
        lastValidHash = null;
        if (!_tree.TryGet(blockHash, out Node node))
        {
            return false;
        }

        lock (node)
        {
            lastValidHash = node.LastValidHash;
            return node.IsInvalid;
        }
    }

    class Node
    {
        public HashSet<Hash256> Children { get; } = [];
        public bool IsInvalid { get; set; }
        /// <summary>Null while invalid means that no validated ancestor is known.</summary>
        public Hash256? LastValidHash { get; set; }
    }

    public void Dispose()
    {
        foreach (Action action in _disposables)
        {
            action.Invoke();
        }
    }
}
