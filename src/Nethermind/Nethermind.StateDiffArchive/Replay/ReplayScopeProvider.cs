// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.StateDiffArchive.Replay;

/// <summary>
/// Decorates the main <see cref="IWorldStateScopeProvider"/> during replay: it publishes each opened scope
/// to the <see cref="ReplayScopeTracker"/> (so <see cref="ReplayBlockProcessor"/> can write the recorded
/// diff into it) and, on commit, verifies the recomputed state root against the recorded one.
/// </summary>
public sealed class ReplayScopeProvider(
    IWorldStateScopeProvider inner,
    ReplayScopeTracker tracker,
    bool verifyStateRoot,
    ILogManager logManager) : IWorldStateScopeProvider
{
    private readonly ILogger _logger = logManager.GetClassLogger<ReplayScopeProvider>();

    public bool HasRoot(BlockHeader? baseBlock) => inner.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        ReplayScope scope = new(inner.BeginScope(baseBlock, metrics), tracker, verifyStateRoot, _logger);
        tracker.Current = scope;
        return scope;
    }

    private sealed class ReplayScope(
        IWorldStateScopeProvider.IScope inner,
        ReplayScopeTracker tracker,
        bool verifyStateRoot,
        ILogger logger) : IWorldStateScopeProvider.IScope
    {
        public Hash256 RootHash => inner.RootHash;
        public void UpdateRootHash() => inner.UpdateRootHash();
        public Account? Get(Address address) => inner.Get(address);
        public void HintGet(Address address, Account? account) => inner.HintGet(address, account);
        public IWorldStateScopeProvider.ICodeDb CodeDb => inner.CodeDb;
        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => inner.CreateStorageTree(address);
        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => inner.StartWriteBatch(estimatedAccountNum);
        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => inner.HintBal(bal, sink);

        public void Commit(ulong blockNumber)
        {
            Hash256? expected = tracker.ExpectedRoot;
            tracker.ExpectedRoot = null;

            if (verifyStateRoot && expected is not null)
            {
                // Verify before committing so a mismatch leaves nothing persisted.
                inner.UpdateRootHash();
                if (inner.RootHash != expected)
                {
                    if (logger.IsError)
                        logger.Error($"StateDiffArchive: replay state-root mismatch at block {blockNumber}: computed {inner.RootHash}, expected {expected}.");
                    throw new StateDiffReplayException(blockNumber, expected, inner.RootHash);
                }
            }

            inner.Commit(blockNumber);
        }

        public void Dispose()
        {
            if (ReferenceEquals(tracker.Current, this)) tracker.Current = null;
            inner.Dispose();
        }
    }
}
