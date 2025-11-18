// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class XdcBlockSuggester : IProducedBlockSuggester
{
    private readonly IBlockTree _blockTree;
    private readonly IBlockProducerRunner _blockProducerRunner;

    //TODO consider getting rid of this entirely and suggest the block directly when building in HotStuff module
    public XdcBlockSuggester(IBlockTree blockTree, IBlockProducerRunner blockProducer)
    {
        _blockTree = blockTree;
        _blockProducerRunner = blockProducer;
        _blockProducerRunner.BlockProduced += OnBlockProduced;
    }

    private void OnBlockProduced(object? sender, BlockEventArgs e)
    {
        _blockTree.SuggestBlock(e.Block);
    }

    public void Dispose() => _blockProducerRunner.BlockProduced -= OnBlockProduced;
}
