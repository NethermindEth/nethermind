// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostBlockImprovementContext : IBlockImprovementContext
{
    private readonly IBoostRelay _boostRelay;
    private readonly IStateReader _stateReader;
    private readonly FeesTracer _feesTracer = new();
    private readonly SharedCancellationTokenSource _improvementCancellation;
    private CancellationTokenSource? _timeOutCancellation;
    private CancellationTokenSource? _linkedCancellation;
    private volatile BlockProductionSnapshot _best;
    private int _disposed;

    // Test seams for pausing a late build after its Disposed check and a disposer before it pins.
    internal Action? PassedDisposedCheck;
    internal Action? Pinning;

    public BoostBlockImprovementContext(Block? currentBestBlock,
        IBlockProducer blockProducer,
        TimeSpan timeout,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        IBoostRelay boostRelay,
        IStateReader stateReader,
        DateTimeOffset startDateTime,
        SharedCancellationTokenSource cts)
    {
        _boostRelay = boostRelay;
        _stateReader = stateReader;
        _improvementCancellation = cts;
        _timeOutCancellation = new CancellationTokenSource(timeout);
        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _timeOutCancellation.Token);
        _best = new(currentBestBlock, UInt256.Zero);
        StartDateTime = startDateTime;
        ImprovementTask = StartImprovingBlock(blockProducer, parentHeader, payloadAttributes, _linkedCancellation.Token);
    }

    private async Task<Block?> StartImprovingBlock(
        IBlockProducer blockProducer,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        CancellationToken cancellationToken)
    {

        payloadAttributes = await _boostRelay.GetPayloadAttributes(payloadAttributes, cancellationToken);
        Address feeRecipient = payloadAttributes.SuggestedFeeRecipient!;
        _stateReader.TryGetAccount(parentHeader, feeRecipient, out AccountStruct account);
        UInt256 balanceBefore = account.Balance;
        Block? block = await blockProducer.BuildBlock(parentHeader, _feesTracer, payloadAttributes, IBlockProducer.Flags.None, cancellationToken);
        if (block is not null && TrySetBest(new(block, _feesTracer.Fees), _best))
        {
            _stateReader.TryGetAccount(parentHeader, feeRecipient, out account);
            await _boostRelay.SendPayload(new BoostExecutionPayloadV1 { Block = ExecutionPayload.Create(block), Profit = account.Balance - balanceBefore }, cancellationToken);
        }

        return _best.CurrentBestBlock;
    }

    // A build that ignored cancellation can finish after the payload was handed out; it must not replace it.
    private bool TrySetBest(BlockProductionSnapshot best, BlockProductionSnapshot expected)
    {
        if (Disposed) return false;
        PassedDisposedCheck?.Invoke();
        return Interlocked.CompareExchange(ref _best, best, expected) == expected;
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

    public Task<Block?> ImprovementTask { get; }
    public BlockProductionSnapshot Best => _best;
    public bool Disposed => Volatile.Read(ref _disposed) != 0;
    public DateTimeOffset StartDateTime { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        PinBest();
        CancellationTokenExtensions.CancelDisposeAndClear(ref _linkedCancellation);
        CancellationTokenExtensions.CancelDisposeAndClear(ref _timeOutCancellation);
    }

    public void CancelOngoingImprovements() => _improvementCancellation.CancelAndDispose();
}
