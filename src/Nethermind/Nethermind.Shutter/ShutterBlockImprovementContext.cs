// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Shutter.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Shutter;

public class ShutterBlockImprovementContextFactory(
    IBlockProducer blockProducer,
    ShutterTxSource shutterTxSource,
    IShutterConfig shutterConfig,
    ShutterTime time,
    ILogManager logManager) : IBlockImprovementContextFactory
{
    public IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime) =>
        new ShutterBlockImprovementContext(blockProducer,
                                           shutterTxSource,
                                           shutterConfig,
                                           time,
                                           currentBestBlock,
                                           parentHeader,
                                           payloadAttributes,
                                           startDateTime,
                                           GnosisSpecProvider.SlotLength,
                                           logManager);
    public bool KeepImproving => false;
}

public class ShutterBlockImprovementContext : IBlockImprovementContext
{
    public Task<Block?> ImprovementTask { get; }

    public Block? CurrentBestBlock { get; private set; }

    public bool Disposed { get; private set; }
    public DateTimeOffset StartDateTime { get; }

    public UInt256 BlockFees => 0;

    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ILogger _logger;
    private readonly IBlockProducer _blockProducer;
    private readonly IShutterTxSignal _txSignal;
    private readonly IShutterConfig _shutterConfig;
    private readonly ShutterTime _time;
    private readonly BlockHeader _parentHeader;
    private readonly PayloadAttributes _payloadAttributes;
    private readonly ulong _slotTimestampMs;
    private readonly TimeSpan _slotLength;

    internal ShutterBlockImprovementContext(
        IBlockProducer blockProducer,
        IShutterTxSignal shutterTxSignal,
        IShutterConfig shutterConfig,
        ShutterTime time,
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        TimeSpan slotLength,
        ILogManager logManager)
    {
        if (slotLength == TimeSpan.Zero)
        {
            throw new ArgumentException("Cannot be zero.", nameof(slotLength));
        }

        _slotTimestampMs = payloadAttributes.Timestamp * 1000;

        _cancellationTokenSource = new CancellationTokenSource();
        CurrentBestBlock = currentBestBlock;
        StartDateTime = startDateTime;
        _logger = logManager.GetClassLogger();
        _blockProducer = blockProducer;
        _txSignal = shutterTxSignal;
        _shutterConfig = shutterConfig;
        _time = time;
        _parentHeader = parentHeader;
        _payloadAttributes = payloadAttributes;
        _slotLength = slotLength;

        ImprovementTask = Task.Run(ImproveBlock);
    }

    public void Dispose()
    {
        Disposed = true;
        CancellationTokenExtensions.CancelDisposeAndClear(ref _cancellationTokenSource);
    }

    private async Task<Block?> ImproveBlock()
    {
        _logger.Debug("Running Shutter block improvement.");

        ulong slot;
        long offset;
        try
        {
            (slot, offset) = _time.GetBuildingSlotAndOffset(_slotTimestampMs);
        }
        catch (ShutterTime.ShutterSlotCalulationException e)
        {
            _logger.Warn($"Could not calculate Shutter building slot: {e}");
            await BuildBlock();
            return CurrentBestBlock;
        }

        bool includedShutterTxs = await TryBuildShutterBlock(slot);
        if (includedShutterTxs)
        {
            return CurrentBestBlock;
        }

        long waitTime = _shutterConfig.MaxKeyDelay - offset;
        if (waitTime <= 0)
        {
            _logger.Warn($"Cannot await Shutter decryption keys for slot {slot}, offset of {offset}ms is too late.");
            return CurrentBestBlock;
        }
        waitTime = Math.Min(waitTime, 2 * (long)_slotLength.TotalMilliseconds);

        _logger.Debug($"Awaiting Shutter decryption keys for {slot} at offset {offset}ms. Timeout in {waitTime}ms...");

        ObjectDisposedException.ThrowIf(_cancellationTokenSource is null, this);

        using var txTimeout = new CancellationTokenSource((int)waitTime);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource!.Token, txTimeout.Token);

        try
        {
            await _txSignal.WaitForTransactions(slot, source.Token);
        }
        catch (OperationCanceledException)
        {
            Metrics.KeysMissed++;
            _logger.Warn($"Shutter decryption keys not received in time for slot {slot}.");

            return CurrentBestBlock;
        }

        // should succeed after waiting for transactions
        await TryBuildShutterBlock(slot);

        return CurrentBestBlock;
    }

    private async Task<bool> TryBuildShutterBlock(ulong slot)
    {
        bool hasShutterTxs = _txSignal.HaveTransactionsArrived(slot);
        await BuildBlock();
        return hasShutterTxs;
    }

    private async Task BuildBlock()
    {
        Block? result = await _blockProducer.BuildBlock(_parentHeader, null, _payloadAttributes, _cancellationTokenSource!.Token);
        if (result is not null)
        {
            CurrentBestBlock = result;
        }
    }

}
