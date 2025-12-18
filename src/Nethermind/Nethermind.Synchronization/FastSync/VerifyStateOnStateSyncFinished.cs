// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State;

namespace Nethermind.Synchronization.FastSync;

public class VerifyStateOnStateSyncFinished
{
    private readonly IVerifyTrieStarter _verifyTrieStarter;
    private readonly ITreeSync _treeSync;

    public VerifyStateOnStateSyncFinished(IVerifyTrieStarter verifyTrieStarter, ITreeSync treeSync)
    {
        _verifyTrieStarter = verifyTrieStarter;
        _treeSync = treeSync;
        _treeSync.SyncCompleted += TreeSyncOnOnVerifyPostSyncCleanup;
    }

    private void TreeSyncOnOnVerifyPostSyncCleanup(object? sender, ITreeSync.SyncCompletedEventArgs evt)
    {
        _treeSync.SyncCompleted -= TreeSyncOnOnVerifyPostSyncCleanup;

        _verifyTrieStarter.TryStartVerifyTrie(evt.Pivot);
    }
}
