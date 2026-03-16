// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Lightweight stub implementations of merge/sync interfaces used by benchmark DI containers.
/// These are registered in <see cref="BenchmarkContainer.BenchmarkOverrideModule"/> so that
/// components like NewPayloadHandler can be auto-wired by Autofac without pulling in the
/// full merge synchronization stack.
/// </summary>
internal static class BenchmarkMergeStubs
{
#nullable enable
    internal sealed class NoopPayloadPreparationService : IPayloadPreparationService
    {
        public string? StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes) => null;

        public ValueTask<IBlockProductionContext?> GetPayload(string payloadId, bool skipCancel = false)
            => ValueTask.FromResult<IBlockProductionContext?>(null);

        public void CancelBlockProduction(string payloadId) { }
    }
#nullable disable

    internal sealed class NoopMergeSyncController : IMergeSyncController
    {
        public void StopSyncing() { }
        public bool TryInitBeaconHeaderSync(BlockHeader blockHeader) => false;
        public void StopBeaconModeControl() { }
    }

    internal sealed class NoopInvalidChainTracker : IInvalidChainTracker
    {
        public void SetChildParent(Hash256 child, Hash256 parent) { }
        public void OnInvalidBlock(Hash256 failedBlock, Hash256 parent) { }
        public bool IsOnKnownInvalidChain(Hash256 blockHash, out Hash256 lastValidHash)
        {
            lastValidHash = null;
            return false;
        }
        public void Dispose() { }
    }

    internal sealed class StaticBeaconPivot : IBeaconPivot
    {
        private readonly BlockHeader _pivot;

        public StaticBeaconPivot(BlockHeader pivot)
        {
            _pivot = pivot;
            ProcessDestination = pivot;
        }

        public long PivotNumber => _pivot.Number;
        public Hash256 PivotHash => _pivot.Hash;
        public Hash256 PivotParentHash => _pivot.ParentHash;
        public long PivotDestinationNumber => _pivot.Number;
        public BlockHeader ProcessDestination { get; set; }
        public bool ShouldForceStartNewSync { get; set; }

        public void EnsurePivot(BlockHeader blockHeader, bool updateOnlyIfNull = false)
        {
            if (!updateOnlyIfNull || ProcessDestination is null)
            {
                ProcessDestination = blockHeader;
            }
        }

        public void RemoveBeaconPivot() { }
        public bool BeaconPivotExists() => true;
    }

    internal sealed class NoopProcessingStats : IProcessingStats
    {
        event EventHandler<BlockStatistics> IProcessingStats.NewProcessingStatistics
        {
            add { }
            remove { }
        }

        public void Start() { }
        public void CaptureStartStats() { }
        public void UpdateStats(Block block, BlockHeader baseBlock, long blockProcessingTimeInMicros) { }
    }
}
