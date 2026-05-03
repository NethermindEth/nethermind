// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.SnapSync;

public class SnapSyncRunner(SimpleDispatcher<SnapSyncBatch> dispatcher, ISnapTrieFactory snapTrieFactory) : ISnapSyncRunner
{
    public async Task Run(CancellationToken token)
    {
        // Sequential lifecycle: init before any batch can create a tree, finalize after all
        // batches have completed and their trees disposed. The new dispatcher.Run guarantees
        // both ordering halves, so the factory doesn't need internal locking for these hooks.
        snapTrieFactory.EnsureInitialize();
        try
        {
            await dispatcher.Run(token);
        }
        finally
        {
            snapTrieFactory.FinalizeSync();
        }
    }
}
