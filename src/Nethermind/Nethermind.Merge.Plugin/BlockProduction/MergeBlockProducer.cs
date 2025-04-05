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

public class MergeBlockProducer : IMergeBlockProducer
{
    private readonly IPoSSwitcher _poSSwitcher;

    public IBlockProducer? PreMergeBlockProducer { get; }
    public IBlockProducer PostMergeBlockProducer { get; }

    private bool HasPreMergeProducer => PreMergeBlockProducer is not null;

    public MergeBlockProducer(IBlockProducer? preMergeBlockProducer, IBlockProducer? postMergeBlockProducer, IPoSSwitcher? poSSwitcher)
    {
        PreMergeBlockProducer = preMergeBlockProducer;
        PostMergeBlockProducer = postMergeBlockProducer ?? throw new ArgumentNullException(nameof(postMergeBlockProducer));

        _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
    }

    public Task<Block?> BuildBlock(BlockHeader? parentHeader, IBlockTracer? blockTracer = null,
        PayloadAttributes? payloadAttributes = null, CancellationToken token = default)
    {
        return _poSSwitcher.HasEverReachedTerminalBlock() || HasPreMergeProducer == false
            ? PostMergeBlockProducer.BuildBlock(parentHeader, blockTracer, payloadAttributes, token)
            : PreMergeBlockProducer!.BuildBlock(parentHeader, blockTracer, payloadAttributes, token);
    }
}
