// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Rebuilds a payload under <see cref="DependencyProofAggregation.BuilderOnly"/> once the EIP-8288 proof its
/// build scheduled is ready, so the payload can carry the aggregated proof.</summary>
/// <remarks>A build under a deadline ships raw witnesses and leaves the proof to a later build, but the payload service
/// rebuilds only when transactions arrive, so a payload over a quiet pool would otherwise never use its proof.</remarks>
public sealed class AggregatingBlockImprovementContextFactory(
    IBlockImprovementContextFactory inner,
    DependencyProofCache proofCache,
    IBlocksConfig blocksConfig,
    ISpecProvider specProvider,
    ILogManager logManager) : IBlockImprovementContextFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<AggregatingBlockImprovementContextFactory>();

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
        return new RebuildingContext(Start, () => inner.StartBlockImprovementContext(null, parentHeader, payloadAttributes, startDateTime, UInt256.Zero, cts), proofCache, _logger);
    }

    private sealed class RebuildingContext : IBlockImprovementContext
    {
        private readonly Lock _lock = new();
        private readonly IBlockImprovementContext _first;
        private readonly Func<IBlockImprovementContext> _rebuild;
        private readonly DependencyProofCache _proofCache;
        private readonly ILogger _logger;
        private ValueHash256? _awaited;
        private IBlockImprovementContext? _second;
        private volatile BlockProductionSnapshot? _best;
        private volatile bool _disposed;

        public RebuildingContext(Func<IBlockImprovementContext> start, Func<IBlockImprovementContext> rebuild, DependencyProofCache proofCache, ILogger logger)
        {
            _rebuild = rebuild;
            _proofCache = proofCache;
            _logger = logger;
            // Subscribed before the build starts, so a proof its build schedules cannot complete unseen.
            proofCache.ScheduledProofCompleted += OnProven;
            try
            {
                _first = start();
            }
            catch
            {
                proofCache.ScheduledProofCompleted -= OnProven;
                throw;
            }

            _first.ImprovementTask.ContinueWith(_ => OnFirstBuilt(), TaskContinuationOptions.OnlyOnRanToCompletion);
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
            _first.Dispose();
            second?.Dispose();
        }

        /// <summary>Awaits the statement of the first build's raw leanSPHINCS witnesses, rebuilding at once if it is proven already.</summary>
        private void OnFirstBuilt()
        {
            if (_first.Best.CurrentBestBlock is not { DependencyProof: [Eip8141Constants.DependencyProofVersionRaw, ..] } block) return;

            List<AggregatedDependency> dependencies = AggregatedDependencyProof.Aggregated(FrameTxDependencyProof.BlockDependencies(block));
            if (dependencies.Count == 0) return;

            ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(dependencies);
            lock (_lock) _awaited = depsHash;
            if (_proofCache.TryGetProof(depsHash, out _)) Rebuild();
        }

        private void OnProven(ValueHash256 depsHash)
        {
            bool awaited;
            lock (_lock) awaited = _awaited == depsHash;
            // Off the prover's thread, which raised the event and would book a failure here against the proof.
            if (awaited) Task.Run(Rebuild);
        }

        private void Rebuild()
        {
            try
            {
                StartRebuild();
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error("EIP-8288 payload rebuild failed", exception);
            }
        }

        private void StartRebuild()
        {
            IBlockImprovementContext second;
            lock (_lock)
            {
                if (_disposed || _second is not null) return;
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
    }
}
