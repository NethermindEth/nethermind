// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;

namespace Nethermind.Synchronization.FastSync;

public class VerifyStateOnStateSyncFinished(IBlockingVerifyTrie blockingVerifyTrie, ITreeSync treeSync) : IStartable
{
    public void Start()
    {
        treeSync.SyncCompleted += TreeSyncOnOnVerifyPostSyncCleanup;
    }

    private void TreeSyncOnOnVerifyPostSyncCleanup(object? sender, ITreeSync.SyncCompletedEventArgs evt)
    {
        treeSync.SyncCompleted -= TreeSyncOnOnVerifyPostSyncCleanup;

        blockingVerifyTrie.TryStartVerifyTrie(evt.Root);
    }
}
