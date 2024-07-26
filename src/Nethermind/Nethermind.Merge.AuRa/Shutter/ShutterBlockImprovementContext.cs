// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.BlockProduction;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Merge.AuRa.Shutter;
public class ShutterBlockImprovementContext : IBlockImprovementContext
{
    private readonly BlockHeader parentHeader;
    private CancellationTokenSource? _cancellationTokenSource;
    private TaskCompletionSource<Block?> _result;

    private readonly ulong _buildSlot;

    public ShutterBlockImprovementContext(
        IBlockProducer blockProducer,
        ShutterTxSource shutterTxSource,
        IShutterConfig shutterConfig,
        Block currentBestBlock,
        TimeSpan timeout,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        ulong genesisTimestamp,
        ushort slotLength)
    {
        _cancellationTokenSource = new CancellationTokenSource(timeout);
        CurrentBestBlock = currentBestBlock;
        this.parentHeader = parentHeader;
        StartDateTime = startDateTime;
        _buildSlot = GetBuildingSlot(payloadAttributes, genesisTimestamp, slotLength);
        _result = new TaskCompletionSource<Block?>();
        ImprovementTask =
        Task.Run(async () =>
        {
            Task timeout = Task.Delay((int)shutterConfig.ExtraBuildWindow);
            Task first = await Task.WhenAny(timeout, shutterTxSource.WaitForTransactions(_buildSlot));
            if (first == timeout)
                return Task.FromResult(currentBestBlock);
            return blockProducer.BuildBlock(parentHeader, null, payloadAttributes, );
        });
    }

    public Task<Block?> ImprovementTask { get; }

    public Block? CurrentBestBlock { get; private set; }

    public bool Disposed { get; private set; }
    public DateTimeOffset StartDateTime { get; }

    public UInt256 BlockFees => 0;

    private static ulong GetBuildingSlot(PayloadAttributes payloadAttributes, ulong genesisTimestamp, ushort slotLength)
    {
        var unixTime = payloadAttributes.Timestamp;
        ulong timeSinceGenesis = unixTime - genesisTimestamp;
        ulong currentSlot = timeSinceGenesis / slotLength;

        return currentSlot;
    }

    public void Dispose()
    {
        Disposed = true;
        _result.TrySetCanceled();
        CancellationTokenExtensions.CancelDisposeAndClear(ref _cancellationTokenSource);
    }
}
