// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool.Test;

/// <summary>
/// A minimal <see cref="IBlockTree"/> for TxPool tests that avoids NSubstitute's static state issues
/// when running tests in parallel.
/// </summary>
internal class TestBlockTree : BlockTreeTestDouble
{
    public override BlockHeader FindBestSuggestedHeader() => BestSuggestedHeader!;

    /// <summary>
    /// Lets a test decouple the best downloaded block from <see cref="BlockTreeTestDouble.Head"/> to model a syncing node.
    /// </summary>
    public ulong? BestKnownNumberOverride { get; set; }

    public override ulong BestKnownNumber => BestKnownNumberOverride ?? base.BestKnownNumber;

    public void HealCanonicalChain(Hash256 startHash, ulong maxBlockDepth) { }

    private readonly Dictionary<Hash256AsKey, Block> _removedBlocks = [];

    /// <summary>A block whose lookup throws, as a corrupt or unreadable one does.</summary>
    public Hash256? UnreadableBlockHash { get; set; }

    public override Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null) =>
        blockHash == UnreadableBlockHash ? throw new RlpException("unreadable block")
        : _removedBlocks.TryGetValue(blockHash, out Block? block) ? block : base.FindBlock(blockHash, options, blockNumber);

    /// <summary>Takes <paramref name="block"/> off the main chain, as a head moving back below it does.</summary>
    public void RaiseBlockRemovedFromMain(Block block)
    {
        _removedBlocks[block.Hash!] = block;
        RaiseBlockRemovedFromMain(new BlockHeaderEventArgs(block.Header));
    }
}
