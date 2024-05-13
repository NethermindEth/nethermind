// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class MergeBlockProducer : IBlockProducer
{
    private readonly IBlockProducer? _preMergeProducer;
    private readonly IBlockProducer _eth2BlockProducer;
    private readonly IPoSSwitcher _poSSwitcher;
    private bool HasPreMergeProducer => _preMergeProducer is not null;

    public MergeBlockProducer(IBlockProducer? preMergeProducer, IBlockProducer? postMergeBlockProducer, IPoSSwitcher? poSSwitcher)
    {
        _preMergeProducer = preMergeProducer;
        _eth2BlockProducer = postMergeBlockProducer ?? throw new ArgumentNullException(nameof(postMergeBlockProducer));
        _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
    }

    public Task<Block?> BuildBlock(BlockHeader? parentHeader, CancellationToken? token, IBlockTracer? blockTracer = null,
        PayloadAttributes? payloadAttributes = null)
    {
        return _poSSwitcher.HasEverReachedTerminalBlock() || HasPreMergeProducer == false
            ? _eth2BlockProducer.BuildBlock(parentHeader, token, blockTracer)
            : _preMergeProducer!.BuildBlock(parentHeader, token, blockTracer);
    }
}
