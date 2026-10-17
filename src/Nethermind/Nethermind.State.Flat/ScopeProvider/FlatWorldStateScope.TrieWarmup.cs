// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed partial class FlatWorldStateScope
{
    internal bool IsWarmupStopped(int sequenceId) => IsDisposed || _pausePrewarmer || _hintSequenceId != sequenceId;

    internal void WarmUpStateTrieBatch(ReadOnlySpan<PatriciaTree.WarmUpEntry> entries, int sequenceId)
    {
        if (IsWarmupStopped(sequenceId) || !_snapshotBundle.TryLeaseReadOnlyBundle()) return;
        try
        {
            WarmUpTrieBatch(Volatile.Read(ref _warmupStateTree) ?? CreateWarmupStateTree(), entries, sequenceId);
        }
        finally
        {
            _snapshotBundle.ReleaseReadOnlyBundleLease();
        }
    }

    internal void WarmUpTrieBatch(PatriciaTree tree, ReadOnlySpan<PatriciaTree.WarmUpEntry> entries, int sequenceId)
    {
        try
        {
            tree.WarmUpPaths(entries, (Scope: this, SequenceId: sequenceId),
                static state => state.Scope.IsWarmupStopped(state.SequenceId));
            return;
        }
        catch (Exception exception) when (exception is NodeHashMismatchException or TrieNodeException) { }

    }

    internal void CompleteWarmups(int count)
    {
        if (Interlocked.Add(ref _outstandingWarmups, -count) == 0) Volatile.Read(ref _warmupsDrained)?.Set();
    }
}
