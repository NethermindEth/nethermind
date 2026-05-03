// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.ParallelSync;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.Synchronization.SnapSync;

public class SnapSyncRunner : ISnapSyncRunner
{
    private readonly Func<CancellationToken, Task> _runDispatcher;
    private readonly ISnapTrieFactory _snapTrieFactory;

    public SnapSyncRunner(SimpleDispatcher<SnapSyncBatch> dispatcher, ISnapTrieFactory snapTrieFactory)
        : this(dispatcher.Run, snapTrieFactory) { }

    internal SnapSyncRunner(Func<CancellationToken, Task> runDispatcher, ISnapTrieFactory snapTrieFactory)
    {
        _runDispatcher = runDispatcher;
        _snapTrieFactory = snapTrieFactory;
    }

    public async Task Run(CancellationToken token)
    {
        _snapTrieFactory.EnsureInitialize();
        try
        {
            await _runDispatcher(token);
        }
        finally
        {
            // Always finalize — FinalizeSync is idempotent and we want partial writes flushed on cancel/throw.
            _snapTrieFactory.FinalizeSync();
        }
    }
}
