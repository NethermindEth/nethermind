// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization
{
    public class No : IBeaconSyncStrategy, ISyncPivotResolver
    {
        private No() { }

        public static No BeaconSync { get; } = new();

        public static No SyncPivot => BeaconSync;

        public bool ShouldBeInBeaconHeaders() => false;

        public bool ShouldBeInBeaconModeControl() => false;

        public bool IsBeaconSyncFinished(BlockHeader? blockHeader) => true;
        public bool MergeTransitionFinished => false;
        public ulong? GetTargetBlockHeight() => null;
        public Hash256? GetFinalizedHash() => null;
        public Hash256? GetHeadBlockHash() => null;

        public Task EnsureSyncPivot(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public interface IBeaconSyncStrategy
    {
        bool ShouldBeInBeaconHeaders();
        bool ShouldBeInBeaconModeControl();
        bool IsBeaconSyncFinished(BlockHeader? blockHeader);

        public bool MergeTransitionFinished { get; }

        public ulong? GetTargetBlockHeight();
        public Hash256? GetFinalizedHash();
        public Hash256? GetHeadBlockHash();
    }

    /// <summary>
    /// Resolves the starting sync pivot before mode selection begins. Awaited once, before
    /// <see cref="ParallelSync.ISyncModeSelector.Start"/>, so that sync feeds never run against a stale pivot.
    /// </summary>
    /// <remarks>The default (non-merge) implementation is a no-op that completes immediately.</remarks>
    public interface ISyncPivotResolver
    {
        /// <summary>
        /// Completes once the sync pivot is resolved from the Consensus Layer, attempts are exhausted
        /// (falling back to the config pivot), or pivot resolution is not applicable to this node.
        /// </summary>
        Task EnsureSyncPivot(CancellationToken cancellationToken);
    }
}
