// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed partial class FlatWorldStateScope
{
    internal readonly record struct TrieWarmUpHint(FlatStorageTree? Storage, PatriciaTree.WarmUpEntry Entry);

    internal void WarmUpTrieCohort(ReadOnlySpan<TrieWarmUpHint> hints, int sequenceId)
    {
        if (IsWarmupStopped(sequenceId) || !_snapshotBundle.TryLeaseReadOnlyBundle()) return;
        try
        {
            using ArrayPoolListRef<PatriciaTree.WarmUpTarget> targets = new(0);
            using ArrayPoolListRef<Hash256?> addresses = new(0);
            Span<PatriciaTree.WarmUpWork> work = stackalloc PatriciaTree.WarmUpWork[PatriciaTree.WarmUpBatchSize];
            int count = 0;
            foreach (TrieWarmUpHint hint in hints)
            {
                if (IsWarmupStopped(sequenceId)) return;
                try
                {
                    PatriciaTree tree = hint.Storage?.GetCohortWarmupTree()
                        ?? (Volatile.Read(ref _warmupStateTree) ?? CreateWarmupStateTree());
                    PatriciaTree.WarmUpEntry entry = hint.Entry;
                    if (count == 0 && tree.TryWarmUpCachedPath(in entry)) continue;
                    int target = 0;
                    while (target < targets.Count && !ReferenceEquals(targets[target].Tree, tree)) target++;
                    if (target == targets.Count)
                    {
                        targets.Add(new(tree, tree.RootRef));
                        addresses.Add(hint.Storage?.WarmupAddress);
                    }
                    work[count++] = new(target, hint.Entry);
                }
                catch (Exception exception) when (IsUnavailableWarmup(exception)) { }
            }
            if (count < PatriciaTree.MinWarmUpBatchSize)
            {
                for (int i = 0; i < count; i++)
                {
                    if (IsWarmupStopped(sequenceId)) return;
                    ReadOnlySpan<PatriciaTree.WarmUpEntry> entry = [work[i].Entry];
                    WarmUpTrieBatch(targets[work[i].Target].Tree, entry, sequenceId);
                }
                return;
            }
            PatriciaTree.WarmUpTargets(targets.AsSpan(), work[..count], new CohortWarmUpReader(this, addresses.AsSpan()),
                (Scope: this, SequenceId: sequenceId), static state => state.Scope.IsWarmupStopped(state.SequenceId));
        }
        finally
        {
            _snapshotBundle.ReleaseReadOnlyBundleLease();
        }
    }

    private bool IsUnavailableWarmup(Exception exception) =>
        exception is TrieNodeException or NodeHashMismatchException or ObjectDisposedException
        || exception is NullReferenceException && IsDisposed;

    private readonly ref struct CohortWarmUpReader(FlatWorldStateScope scope, ReadOnlySpan<Hash256?> addresses)
        : PatriciaTree.IWarmUpReader
    {
        private readonly ReadOnlySpan<Hash256?> _addresses = addresses;

        public void LoadRlps(scoped ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, scoped ReadOnlySpan<int> targets, scoped Span<byte[]?> values)
        {
            using ArrayPoolListRef<(Hash256? Address, TreePath Path)> inputs = new(requests.Length, requests.Length);
            for (int i = 0; i < requests.Length; i++) inputs[i] = (_addresses[targets[i]], requests[i].Path);
            scope._snapshotBundle.LoadRlps(inputs.AsSpan(), values);
            for (int i = 0; i < requests.Length; i++)
                values[i] = StateTrieStoreWarmerAdapter.GetMatchingRlp(values[i], requests[i].Hash);
        }

        public bool IsUnavailable(Exception exception) => scope.IsUnavailableWarmup(exception);
    }

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
