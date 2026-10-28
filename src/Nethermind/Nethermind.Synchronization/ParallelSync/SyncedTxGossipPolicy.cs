// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.TxPool;

namespace Nethermind.Synchronization.ParallelSync;

public class SyncedTxGossipPolicy : ITxGossipPolicy, IDisposable
{
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly bool _acceptTxWhenNotSynced;
    private volatile bool _shouldListen;

    public SyncedTxGossipPolicy(ISyncModeSelector syncModeSelector)
        : this(syncModeSelector, new TxPoolConfig())
    {
    }

    public SyncedTxGossipPolicy(ISyncModeSelector syncModeSelector, ITxPoolConfig txPoolConfig)
    {
        _syncModeSelector = syncModeSelector;
        _acceptTxWhenNotSynced = txPoolConfig.AcceptTxWhenNotSynced;
        _shouldListen = ShouldListen(syncModeSelector.Current);
        syncModeSelector.Changed += OnSyncModeChanged;
    }

    public bool ShouldListenToGossipedTransactions => _shouldListen;

    public void Dispose() => _syncModeSelector.Changed -= OnSyncModeChanged;

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e) => _shouldListen = ShouldListen(e.Current);

    private bool ShouldListen(SyncMode syncMode) =>
        _acceptTxWhenNotSynced || (syncMode & SyncMode.WaitingForBlock) != 0;
}
