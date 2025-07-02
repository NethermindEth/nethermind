// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class MergeBlockProducerRunner : IBlockProducerRunner
{
    private readonly IBlockProducerRunner? _preMergeProducerRunner;
    private readonly IBlockProducerRunner _postMergeProducerRunner;
    private readonly IPoSSwitcher _poSSwitcher;
    private bool HasPreMergeProducerRunner => _preMergeProducerRunner is not null;

    public MergeBlockProducerRunner(IBlockProducerRunner? preMergeProducerRunner, IBlockProducerRunner? postMergeProducerRunner, IPoSSwitcher? poSSwitcher)
    {
        _preMergeProducerRunner = preMergeProducerRunner;
        _postMergeProducerRunner = postMergeProducerRunner ?? throw new ArgumentNullException(nameof(postMergeProducerRunner));
        _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        _poSSwitcher.TerminalBlockReached += OnSwitchHappened;
        if (HasPreMergeProducerRunner)
            _preMergeProducerRunner!.BlockProduced += OnBlockProduced;

        postMergeProducerRunner.BlockProduced += OnBlockProduced;
    }

    private void OnBlockProduced(object? sender, BlockEventArgs e)
    {
        BlockProduced?.Invoke(this, e);
    }

    private void OnSwitchHappened(object? sender, EventArgs e)
    {
        _preMergeProducerRunner?.StopAsync();
    }

    public void Start()
    {
        _postMergeProducerRunner.Start();
        if (_poSSwitcher.HasEverReachedTerminalBlock() == false && HasPreMergeProducerRunner)
        {
            _preMergeProducerRunner!.Start();
        }
    }

    public async Task StopAsync()
    {
        await _postMergeProducerRunner.StopAsync();
        if (_poSSwitcher.HasEverReachedTerminalBlock() && HasPreMergeProducerRunner)
            await _preMergeProducerRunner!.StopAsync();
    }

    public bool IsProducingBlocks(ulong? maxProducingInterval)
    {
        return _poSSwitcher.HasEverReachedTerminalBlock() || HasPreMergeProducerRunner == false
            ? _postMergeProducerRunner.IsProducingBlocks(maxProducingInterval)
            : _preMergeProducerRunner!.IsProducingBlocks(maxProducingInterval);
    }

    public event EventHandler<BlockEventArgs>? BlockProduced;
}
