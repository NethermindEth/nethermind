// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class MergeBlockProducerRunner : IBlockProducerRunner
{
    private readonly IBlockProducerRunner? _preMergeProducer;
    private readonly IBlockProducerRunner _eth2BlockProducer;
    private readonly IPoSSwitcher _poSSwitcher;
    private bool HasPreMergeProducer => _preMergeProducer is not null;

    public MergeBlockProducerRunner(IBlockProducerRunner? preMergeProducer, IBlockProducerRunner? postMergeBlockProducer, IPoSSwitcher? poSSwitcher)
    {
        _preMergeProducer = preMergeProducer;
        _eth2BlockProducer = postMergeBlockProducer ?? throw new ArgumentNullException(nameof(postMergeBlockProducer));
        _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        _poSSwitcher.TerminalBlockReached += OnSwitchHappened;
        if (HasPreMergeProducer)
            _preMergeProducer!.BlockProduced += OnBlockProduced;

        postMergeBlockProducer.BlockProduced += OnBlockProduced;
    }

    private void OnBlockProduced(object? sender, BlockEventArgs e)
    {
        BlockProduced?.Invoke(this, e);
    }

    private void OnSwitchHappened(object? sender, EventArgs e)
    {
        _preMergeProducer?.StopAsync();
    }

    public void Start()
    {
        _eth2BlockProducer.Start();
        if (_poSSwitcher.HasEverReachedTerminalBlock() == false && HasPreMergeProducer)
        {
            _preMergeProducer!.Start();
        }
    }

    public async Task StopAsync()
    {
        await _eth2BlockProducer.StopAsync();
        if (_poSSwitcher.HasEverReachedTerminalBlock() && HasPreMergeProducer)
            await _preMergeProducer!.StopAsync();
    }

    public bool IsProducingBlocks(ulong? maxProducingInterval)
    {
        return _poSSwitcher.HasEverReachedTerminalBlock() || HasPreMergeProducer == false
            ? _eth2BlockProducer.IsProducingBlocks(maxProducingInterval)
            : _preMergeProducer!.IsProducingBlocks(maxProducingInterval);
    }

    public event EventHandler<BlockEventArgs>? BlockProduced;
}
