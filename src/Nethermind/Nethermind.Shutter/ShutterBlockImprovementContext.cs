// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Shutter.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
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
        Block? currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        UInt256 currentBlockFees,
        SharedCancellationTokenSource cts) =>
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

    public BlockProductionSnapshot Best => _best;

    public bool Disposed => Volatile.Read(ref _disposed) != 0;
    public DateTimeOffset StartDateTime { get; }

    private volatile BlockProductionSnapshot _best;
    private int _disposed;

    // Test seams for pausing a late build after its Disposed check and a disposer before it pins.
    internal Action? PassedDisposedCheck;
    internal Action? Pinning;

    private readonly SharedCancellationTokenSource _improvementCancellation;
    private readonly CancellationToken _improvementToken;
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
        Block? currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        TimeSpan slotLength,
        ILogManager logManager,
        SharedCancellationTokenSource cts)
    {
        if (slotLength == TimeSpan.Zero)
        {
            throw new ArgumentException("Cannot be zero.", nameof(slotLength));
        }

        _slotTimestampMs = payloadAttributes.Timestamp * 1000;

        _improvementCancellation = cts;
        _improvementToken = cts.Token;
        _best = new(currentBestBlock, UInt256.Zero);
        StartDateTime = startDateTime;
        _logger = logManager.GetClassLogger<ShutterBlockImprovementContext>();
        _blockProducer = blockProducer;
        _txSignal = shutterTxSignal;
        _shutterConfig = shutterConfig;
        _time = time;
        _parentHeader = parentHeader;
        _payloadAttributes = payloadAttributes;
        _slotLength = slotLength;

        ImprovementTask = Task.Run(ImproveBlock);
    }

    public void CancelOngoingImprovements() => _improvementCancellation.CancelAndDispose();

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        PinBest();
        CancellationTokenExtensions.CancelDisposeAndClear(ref _linkedCancellation);
    }

    // Swaps in an equal snapshot so a TrySetBest that passed the Disposed check before it was set fails its CAS.
    // Every disposer pins: one returning early could hand out a snapshot that a pending CAS then replaces.
    private void PinBest()
    {
        Pinning?.Invoke();
        BlockProductionSnapshot best;
        do best = _best;
        while (Interlocked.CompareExchange(ref _best, new(best.CurrentBestBlock, best.BlockFees), best) != best);
    }

    // A build that ignored cancellation can finish after the payload was handed out; it must not replace it.
    private bool TrySetBest(BlockProductionSnapshot best, BlockProductionSnapshot expected)
    {
        if (Disposed) return false;
        PassedDisposedCheck?.Invoke();
        return Interlocked.CompareExchange(ref _best, best, expected) == expected;
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
        catch (SlotTime.SlotCalculationException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not calculate Shutter building slot: {e}");
            await BuildBlock();
            return _best.CurrentBestBlock;
        }

        bool includedShutterTxs = await TryBuildShutterBlock(slot);
        if (includedShutterTxs)
        {
            return _best.CurrentBestBlock;
        }

        long waitTime = _shutterConfig.MaxKeyDelay - offset;
        if (waitTime <= 0)
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot await Shutter decryption keys for slot {slot}, offset of {offset}ms is too late.");
            return _best.CurrentBestBlock;
        }
        waitTime = Math.Min(waitTime, 2 * (long)_slotLength.TotalMilliseconds);

        if (_logger.IsDebug) _logger.Debug($"Awaiting Shutter decryption keys for {slot} at offset {offset}ms. Timeout in {waitTime}ms...");

        using CancellationTokenSource txTimeout = new((int)waitTime);
        CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_improvementToken, txTimeout.Token);
        _linkedCancellation = linkedCancellation;

        try
        {
            linkedCancellation.Token.ThrowIfCancellationRequested();
            await _txSignal.WaitForTransactions(slot, linkedCancellation.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            Metrics.IncrementShutterKeysMissed();
            if (_logger.IsWarn) _logger.Warn($"Shutter decryption keys not received in time for slot {slot}.");

            return _best.CurrentBestBlock;
        }

        // should succeed after waiting for transactions
        await TryBuildShutterBlock(slot);

        return _best.CurrentBestBlock;
    }

    private async Task<bool> TryBuildShutterBlock(ulong slot)
    {
        bool hasShutterTxs = _txSignal.HaveTransactionsArrived(slot);
        await BuildBlock();
        return hasShutterTxs;
    }

    private async Task BuildBlock()
    {
        Block? result = await _blockProducer.BuildBlock(_parentHeader, null, _payloadAttributes, IBlockProducer.Flags.None, _linkedCancellation?.Token ?? default);
        if (result is not null)
        {
            TrySetBest(new(result, UInt256.Zero), _best);
        }
    }
}
