// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.TxPool;

namespace Nethermind.Synchronization.ParallelSync;

public class SyncedTxGossipPolicy : ITxGossipPolicy, IDisposable
{
    private readonly ISyncModeSelector _syncModeSelector;
    private volatile bool _shouldListen;

    public SyncedTxGossipPolicy(ISyncModeSelector syncModeSelector)
    {
        _syncModeSelector = syncModeSelector;
        _shouldListen = (syncModeSelector.Current & SyncMode.WaitingForBlock) != 0;
        syncModeSelector.Changed += OnSyncModeChanged;
    }

    public bool ShouldListenToGossipedTransactions => _shouldListen;

    public void Dispose() => _syncModeSelector.Changed -= OnSyncModeChanged;

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
    {
        bool shouldListen = (e.Current & SyncMode.WaitingForBlock) != 0;
        if (shouldListen != _shouldListen)
        {
            _shouldListen = shouldListen;
        }
    }
}
