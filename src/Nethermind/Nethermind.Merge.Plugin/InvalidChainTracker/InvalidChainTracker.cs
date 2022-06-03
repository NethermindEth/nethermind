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

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

/// <summary>
/// Something that tracks if a given hash is on a known invalid chain, as one if it's ancestor have been reported to
/// be invalid.
/// 
/// </summary>
public class InvalidChainTracker: IInvalidChainTracker
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IBlockFinder? _blockFinder;
    private readonly Logging.ILogger _logger;
    private readonly object _opLock = new();
    private readonly LruCache<Keccak, Node> _tree;

    public InvalidChainTracker(IPoSSwitcher poSSwitcher, IBlockFinder blockFinder, ILogManager logManager)
    {
        _poSSwitcher = poSSwitcher;
        _blockFinder = blockFinder;
        _tree = new LruCache<Keccak, Node>(1024, nameof(InvalidChainTracker));
        _logger = logManager.GetClassLogger<InvalidChainTracker>();
    }
    
    public InvalidChainTracker(int maxKeyHandle, IPoSSwitcher poSSwitcher) {
        _poSSwitcher = poSSwitcher;
        _blockFinder = null;
        _tree = new LruCache<Keccak, Node>(maxKeyHandle, nameof(InvalidChainTracker));
        _logger = NullLogger.Instance;
    }

    public void SetChildParent(Keccak child, Keccak parent)
    {
        lock (_opLock)
        {
            Node parentNode = GetNode(parent);

            parentNode.Childs.Add(child);
            if (parentNode.LastValidHash != null)
            {
                PropagateLastValidHash(parentNode);
            }
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
        foreach (Keccak nodeChild in node.Childs)
        {
            Node childNode = GetNode(nodeChild);
            if (childNode.LastValidHash != node.LastValidHash)
            {
                childNode.LastValidHash = node.LastValidHash;
                PropagateLastValidHash(childNode);
            }
        }
    }

    public void OnInvalidBlock(Keccak failedBlock, Keccak parent)
    {
        Keccak effectiveParent = parent;
        BlockHeader? parentHeader = _blockFinder?.FindHeader(parent);
        if (parentHeader != null)
        {
            if (!_poSSwitcher.IsPostMerge(parentHeader))
            {
                effectiveParent = Keccak.Zero;
            }
            else
            {
                _logger.Info("Parent is post merge");
            }
        }
        else
        {
            _logger.Info("Parent not found");
        }

        lock (_opLock)
        {
            Node failedBlockNode = GetNode(failedBlock);
            failedBlockNode.LastValidHash = effectiveParent;
            PropagateLastValidHash(failedBlockNode);
        }
    }

    public bool IsOnKnownInvalidChain(Keccak blockHash, out Keccak? lastValidHash)
    {
        lock (_opLock)
        {
            lastValidHash = null;
            Node node = GetNode(blockHash);
            if (node.LastValidHash != null)
            {
                lastValidHash = node.LastValidHash;
            }

            return node.LastValidHash != null;
        }
    }

    class Node
    {
        public List<Keccak> Childs { get; set; } = new();
        public Keccak? LastValidHash { get; set; }
    }
}
