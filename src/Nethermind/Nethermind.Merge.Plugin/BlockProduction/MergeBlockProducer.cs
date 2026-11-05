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

public class MergeBlockProducer(IBlockProducer? preMergeBlockProducer, IBlockProducer? postMergeBlockProducer, IPoSSwitcher? poSSwitcher) : IMergeBlockProducer
{
    private readonly IPoSSwitcher _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));

    public IBlockProducer? PreMergeBlockProducer { get; } = preMergeBlockProducer;
    public IBlockProducer PostMergeBlockProducer { get; } = postMergeBlockProducer ?? throw new ArgumentNullException(nameof(postMergeBlockProducer));

    private bool HasPreMergeProducer => PreMergeBlockProducer is not null;

    public Task<Block?> BuildBlock(BlockHeader? parentHeader, IBlockTracer? blockTracer = null,
        PayloadAttributes? payloadAttributes = null, IBlockProducer.Flags flags = IBlockProducer.Flags.None, CancellationToken token = default) => _poSSwitcher.HasEverReachedTerminalBlock() || !HasPreMergeProducer
            ? PostMergeBlockProducer.BuildBlock(parentHeader, blockTracer, payloadAttributes, flags, token)
            : PreMergeBlockProducer!.BuildBlock(parentHeader, blockTracer, payloadAttributes, flags, token);
}
