// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.SnapSync;

public class SnapSyncRunner(Func<CancellationToken, Task> runDispatcher, ISnapTrieFactory snapTrieFactory) : ISnapSyncRunner
{
    public SnapSyncRunner(SimpleDispatcher<SnapSyncBatch> dispatcher, ISnapTrieFactory snapTrieFactory)
        : this(dispatcher.Run, snapTrieFactory) { }

    public async Task Run(CancellationToken token)
    {
        snapTrieFactory.EnsureInitialize();
        try
        {
            await runDispatcher(token);
        }
        finally
        {
            // Always finalize — FinalizeSync is idempotent and we want partial writes flushed on cancel/throw.
            snapTrieFactory.FinalizeSync();
        }
    }
}
