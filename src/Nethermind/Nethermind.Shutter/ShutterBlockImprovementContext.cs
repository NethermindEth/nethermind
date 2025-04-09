// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Shutter.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Shutter;

public class ShutterBlockImprovementContextFactory(
    IBlockProducer blockProducer,
    ShutterTxSource shutterTxSource,
    IShutterConfig shutterConfig,
    SlotTime time,
    ILogManager logManager,
    TimeSpan slotLength) : IBlockImprovementContextFactory
{
    public IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        UInt256 currentBlockFees,
        CancellationTokenSource cts) =>
        new ShutterBlockImprovementContext(blockProducer,
                                           shutterTxSource,
                                           shutterConfig,
                                           time,
                                           currentBestBlock,
                                           parentHeader,
                                           payloadAttributes,
                                           startDateTime,
                                           slotLength,
                                           logManager,
                                           cts);
}

public class ShutterBlockImprovementContext : IBlockImprovementContext
{
    public Task<Block?> ImprovementTask { get; }

    public Block? CurrentBestBlock { get; private set; }

    public bool Disposed { get; private set; }
    public DateTimeOffset StartDateTime { get; }

    public UInt256 BlockFees => 0;

    private readonly CancellationTokenSource _improvementCancellation;
    private CancellationTokenSource? _linkedCancellation;
    private readonly ILogger _logger;
    private readonly IBlockProducer _blockProducer;
    private readonly IShutterTxSignal _txSignal;
    private readonly IShutterConfig _shutterConfig;
    private readonly SlotTime _time;
    private readonly BlockHeader _parentHeader;
    private readonly PayloadAttributes _payloadAttributes;
    private readonly ulong _slotTimestampMs;
    private readonly TimeSpan _slotLength;

    internal ShutterBlockImprovementContext(
        IBlockProducer blockProducer,
        IShutterTxSignal shutterTxSignal,
        IShutterConfig shutterConfig,
        SlotTime time,
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        TimeSpan slotLength,
        ILogManager logManager,
        CancellationTokenSource cts)
    {
        if (slotLength == TimeSpan.Zero)
        {
            throw new ArgumentException("Cannot be zero.", nameof(slotLength));
        }

        _slotTimestampMs = payloadAttributes.Timestamp * 1000;

        _improvementCancellation = cts;
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

    public void CancelOngoingImprovements() => _improvementCancellation.Cancel();

    public void Dispose()
    {
        Disposed = true;
        CancellationTokenExtensions.CancelDisposeAndClear(ref _linkedCancellation);
    }

    private async Task<Block?> ImproveBlock()
    {
        if (_logger.IsDebug) _logger.Debug("Running Shutter block improvement.");

        ulong slot;
        long offset;
        try
        {
            (slot, offset) = _time.GetBuildingSlotAndOffset(_slotTimestampMs);
        }
        catch (SlotTime.SlotCalulationException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not calculate Shutter building slot: {e}");
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
            if (_logger.IsWarn) _logger.Warn($"Cannot await Shutter decryption keys for slot {slot}, offset of {offset}ms is too late.");
            return CurrentBestBlock;
        }
        waitTime = Math.Min(waitTime, 2 * (long)_slotLength.TotalMilliseconds);

        if (_logger.IsDebug) _logger.Debug($"Awaiting Shutter decryption keys for {slot} at offset {offset}ms. Timeout in {waitTime}ms...");

        using var txTimeout = new CancellationTokenSource((int)waitTime);
        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_improvementCancellation.Token, txTimeout.Token);

        try
        {
            _linkedCancellation.Token.ThrowIfCancellationRequested();
            await _txSignal.WaitForTransactions(slot, _linkedCancellation.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            Metrics.ShutterKeysMissed++;
            if (_logger.IsWarn) _logger.Warn($"Shutter decryption keys not received in time for slot {slot}.");

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
        Block? result = await _blockProducer.BuildBlock(_parentHeader, null, _payloadAttributes, _linkedCancellation?.Token ?? default);
        if (result is not null)
        {
            CurrentBestBlock = result;
        }
    }
}
