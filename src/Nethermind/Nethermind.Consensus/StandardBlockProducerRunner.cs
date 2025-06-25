// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Consensus;

public class StandardBlockProducerRunner(IBlockProductionTrigger trigger, IBlockTree blockTree, IBlockProducer blockProducer) : IBlockProducerRunner
{
    private bool _isRunning;
    private CancellationTokenSource? _producerCancellationToken;
    private DateTime _lastProducedBlockDateTime;
    public ILogger Logger { get; }

    private void OnTriggerBlockProduction(object? sender, BlockProductionEventArgs e)
    {
        BlockHeader? parent = blockTree.GetProducedBlockParent(e.ParentHeader);
        e.BlockProductionTask = TryProduceAndAnnounceNewBlock(e.CancellationToken, parent, e.BlockTracer, e.PayloadAttributes);
    }

    private async Task<Block?> TryProduceAndAnnounceNewBlock(CancellationToken token, BlockHeader? parentHeader, IBlockTracer? blockTracer = null, PayloadAttributes? payloadAttributes = null)
    {
        using CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, _producerCancellationToken!.Token);
        token = tokenSource.Token;

        Block? block = null;
        try
        {
            block = await blockProducer.BuildBlock(parentHeader, blockTracer, payloadAttributes, token);
            if (block is not null)
            {
                _lastProducedBlockDateTime = DateTime.UtcNow;
                BlockProduced?.Invoke(this, new BlockEventArgs(block));
            }
        }
        catch (Exception e) when (e is not TaskCanceledException)
        {
            if (Logger.IsError) Logger.Error("Failed to produce block", e);
            Metrics.FailedBlockSeals++;
            throw;
        }

        return block;
    }

    public virtual void Start()
    {
        _producerCancellationToken = new CancellationTokenSource();
        _isRunning = true;
        trigger.TriggerBlockProduction += OnTriggerBlockProduction;
        _lastProducedBlockDateTime = DateTime.UtcNow;
    }

    public virtual Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;
        _producerCancellationToken?.Cancel();
        _isRunning = false;
        trigger.TriggerBlockProduction -= OnTriggerBlockProduction;
        _producerCancellationToken?.Dispose();
        return Task.CompletedTask;
    }

    protected virtual bool IsRunning() => _isRunning;

    public bool IsProducingBlocks(ulong? maxProducingInterval)
    {
        if (Logger.IsTrace) Logger.Trace($"Checking IsProducingBlocks: maxProducingInterval {maxProducingInterval}, _lastProducedBlock {_lastProducedBlockDateTime}, IsRunning() {IsRunning()}");
        return IsRunning() && (maxProducingInterval is null || _lastProducedBlockDateTime.AddSeconds(maxProducingInterval.Value) > DateTime.UtcNow);
    }

    public event EventHandler<BlockEventArgs>? BlockProduced;
}
