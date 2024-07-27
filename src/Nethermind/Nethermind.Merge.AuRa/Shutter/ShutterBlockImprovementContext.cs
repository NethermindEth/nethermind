// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Specs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Merge.AuRa.Shutter;
public class ShutterBlockImprovementContextFactory(IBlockProducer blockProducer, ShutterTxSource shutterTxSource, IShutterConfig shutterConfig, ISpecProvider spec, TimeSpan timeout) : IBlockImprovementContextFactory
{
    private readonly ulong genesisTimestamp = 1000 * (spec.ChainId == BlockchainIds.Chiado ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp);

    public IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime) =>
        new ShutterBlockImprovementContext(blockProducer, shutterTxSource, shutterConfig, currentBestBlock, parentHeader, payloadAttributes, startDateTime, timeout, genesisTimestamp, spec.SlotLength??TimeSpan.FromSeconds(5));
}

public class ShutterBlockImprovementContext : IBlockImprovementContext
{
    private CancellationTokenSource? _cancellationTokenSource;

    internal ShutterBlockImprovementContext(
        IBlockProducer blockProducer,
        IShutterTxSignal shutterTxSignal,
        IShutterConfig shutterConfig,
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        TimeSpan timeout,
        ulong genesisTimestamp,
        TimeSpan slotLength)
    {
        if (slotLength == TimeSpan.Zero)
            throw new ArgumentException("Cannot be zero.",nameof(slotLength));
        if (payloadAttributes.Timestamp < genesisTimestamp)
            throw new ArgumentOutOfRangeException(nameof(genesisTimestamp), genesisTimestamp, "Genesis cannot be after the payload timestamp.");

        _cancellationTokenSource = new CancellationTokenSource(timeout);
        CurrentBestBlock = currentBestBlock;
        StartDateTime = startDateTime;
        ImprovementTask =
        Task.Run(async () =>
        {
            (int slot, int offset) = GetBuildingSlotAndOffset(payloadAttributes.Timestamp, genesisTimestamp, slotLength);
            int waitTime = (int)shutterConfig.ExtraBuildWindow - offset;
            if (waitTime < 1)
            {
                return currentBestBlock;
            }
            Task timeout = Task.Delay(waitTime, _cancellationTokenSource.Token);
            Task first = await Task.WhenAny(timeout, shutterTxSignal.WaitForTransactions((ulong)slot));
            if (first == timeout)
                return currentBestBlock;
            Block? result = await blockProducer.BuildBlock(parentHeader, null, payloadAttributes, _cancellationTokenSource.Token);
            if (result !=null)
                CurrentBestBlock = result;
            return result;
        });
    }

    public Task<Block?> ImprovementTask { get; }

    public Block? CurrentBestBlock { get; private set; }

    public bool Disposed { get; private set; }
    public DateTimeOffset StartDateTime { get; }

    public UInt256 BlockFees => 0;

    private static (int, int) GetBuildingSlotAndOffset(ulong currentTimestamp, ulong genesisTimestamp, TimeSpan slotLength)
    {
        double timeSinceGenesis = 1000 * ( currentTimestamp - genesisTimestamp);
        int currentSlot = (int)(timeSinceGenesis / slotLength.TotalMilliseconds);
        int slotOffset = (int)(timeSinceGenesis % slotLength.TotalMilliseconds);

        return (currentSlot, slotOffset);
    }

    public void Dispose()
    {
        Disposed = true;
        CancellationTokenExtensions.CancelDisposeAndClear(ref _cancellationTokenSource);
    }
}
