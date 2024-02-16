// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TxPool;

namespace Nethermind.Synchronization.ParallelSync;

public class SyncedTxGossipPolicy : ITxGossipPolicy
{
    private readonly ISyncModeSelector _syncModeSelector;

    public SyncedTxGossipPolicy(ISyncModeSelector syncModeSelector)
    {
        _syncModeSelector = syncModeSelector;
    }

    public bool ShouldListenToGossipedTransactions => (_syncModeSelector.Current & SyncMode.WaitingForBlock) != 0;
}
