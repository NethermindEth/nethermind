// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.TxPool;

namespace Nethermind.Synchronization.ParallelSync;

public class SyncedTxGossipPolicy : ITxGossipPolicy
{
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly Func<bool> _isProcessingBlock;

    public SyncedTxGossipPolicy(ISyncModeSelector syncModeSelector, Func<bool> isProcessingBlock)
    {
        _syncModeSelector = syncModeSelector;
        _isProcessingBlock = isProcessingBlock;
    }

    public bool ShouldListenToGossipedTransactions => (_syncModeSelector.Current & SyncMode.WaitingForBlock) != 0 && !_isProcessingBlock();
}
