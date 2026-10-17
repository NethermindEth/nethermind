// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed partial class FlatStorageTree
{
    internal bool IsWarmupStopped(int sequenceId) => _scope.IsWarmupStopped(sequenceId);

    internal void WarmUpStorageTrieBatch(ReadOnlySpan<PatriciaTree.WarmUpEntry> entries, int sequenceId)
    {
        if (_scope.IsWarmupStopped(sequenceId) || !_bundle.TryLeaseReadOnlyBundle()) return;
        try
        {
            _scope.WarmUpTrieBatch(GetTrees().Warmup, entries, sequenceId);
        }
        finally
        {
            _bundle.ReleaseReadOnlyBundleLease();
        }
    }

    internal void CompleteWarmups(int count) => _scope.CompleteWarmups(count);
}
