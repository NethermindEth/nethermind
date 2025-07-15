// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.TxPool;

namespace Nethermind.Synchronization.ParallelSync;

public class SyncedTxGossipPolicy : ITxGossipPolicy
{
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly Func<bool> _isProcessing;

    public SyncedTxGossipPolicy(ISyncModeSelector syncModeSelector, Func<bool> isProcessing)
    {
        _syncModeSelector = syncModeSelector;
        _isProcessing = isProcessing;
    }

    public bool ShouldListenToGossipedTransactions => (_syncModeSelector.Current & SyncMode.WaitingForBlock) != 0 && !_isProcessing();
}
