// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Int256;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Rebuilds a payload under <see cref="DependencyProofAggregation.BuilderOnly"/> once the EIP-8288 proof its
/// build scheduled is ready, so the payload can carry the aggregated proof.</summary>
/// <remarks>A build under a deadline ships raw witnesses and leaves the proof to a later build, but the payload service
/// rebuilds only when transactions arrive, so a payload over a quiet pool would otherwise never use its proof.</remarks>
public sealed class AggregatingBlockImprovementContextFactory(
    IBlockImprovementContextFactory inner,
    DependencyProofCache proofCache,
    IBlocksConfig blocksConfig,
    ISpecProvider specProvider) : IBlockImprovementContextFactory
{
    public IBlockImprovementContext StartBlockImprovementContext(Block? currentBestBlock, BlockHeader parentHeader,
        PayloadAttributes payloadAttributes, DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts)
    {
        IBlockImprovementContext Start() =>
            inner.StartBlockImprovementContext(currentBestBlock, parentHeader, payloadAttributes, startDateTime, currentBlockFees, cts);

        if (blocksConfig.DependencyProofAggregation != DependencyProofAggregation.BuilderOnly
            || !specProvider.GetSpec(parentHeader.Number + 1, payloadAttributes.Timestamp).IsEip8288Enabled)
        {
            return Start();
        }

        // Built from scratch, so a rebuild of the same transactions is not dismissed as no better than the build it replaces.
        return new RebuildingContext(Start, () => inner.StartBlockImprovementContext(null, parentHeader, payloadAttributes, startDateTime, UInt256.Zero, cts), proofCache);
    }

    private sealed class RebuildingContext : IBlockImprovementContext
    {
        private readonly Lock _lock = new();
        private readonly IBlockImprovementContext _first;
        private readonly Func<IBlockImprovementContext> _rebuild;
        private readonly DependencyProofCache _proofCache;
        private readonly TaskCompletionSource _proven = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IBlockImprovementContext? _second;
        private volatile BlockProductionSnapshot? _best;
        private volatile bool _disposed;

        public RebuildingContext(Func<IBlockImprovementContext> start, Func<IBlockImprovementContext> rebuild, DependencyProofCache proofCache)
        {
            _rebuild = rebuild;
            _proofCache = proofCache;
            // Subscribed before the build starts, so a proof its build schedules cannot complete unseen.
            proofCache.ScheduledProofCompleted += OnProven;
            _first = start();
            Task.WhenAll(_first.ImprovementTask, _proven.Task).ContinueWith(_ => Rebuild(), TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        public BlockProductionSnapshot Best => _best ?? _first.Best;
        public Task<Block?> ImprovementTask => _first.ImprovementTask;
        public bool Disposed => _disposed;
        public DateTimeOffset StartDateTime => _first.StartDateTime;

        public void CancelOngoingImprovements() => _first.CancelOngoingImprovements();

        public void Dispose()
        {
            IBlockImprovementContext? second;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                second = _second;
            }

            _proofCache.ScheduledProofCompleted -= OnProven;
            _proven.TrySetCanceled();
            _first.Dispose();
            second?.Dispose();
        }

        private void OnProven() => _proven.TrySetResult();

        private void Rebuild()
        {
            IBlockImprovementContext second;
            lock (_lock)
            {
                if (_disposed || !ShippedRawLeanSphincsWitnesses(_first.Best.CurrentBestBlock)) return;
                second = _second = _rebuild();
            }

            second.ImprovementTask.ContinueWith(_ => Adopt(second.Best), TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        private void Adopt(BlockProductionSnapshot rebuilt)
        {
            BlockProductionSnapshot first = _first.Best;
            if (rebuilt.CurrentBestBlock is null || rebuilt.BlockFees < first.BlockFees
                || (rebuilt.BlockFees == first.BlockFees && rebuilt.CurrentBestBlock.GasUsed < (first.CurrentBestBlock?.GasUsed ?? 0)))
            {
                return;
            }

            lock (_lock)
            {
                if (!_disposed) _best = rebuilt;
            }
        }

        private static bool ShippedRawLeanSphincsWitnesses(Block? block) =>
            block?.DependencyProof is [Eip8141Constants.DependencyProofVersionRaw, ..]
            && AggregatedDependencyProof.Aggregated(FrameTxDependencyProof.BlockDependencies(block)).Count > 0;
    }
}
