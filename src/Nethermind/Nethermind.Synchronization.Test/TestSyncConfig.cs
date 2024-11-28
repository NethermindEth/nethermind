// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;

namespace Nethermind.Synchronization.Test;

public class TestSyncConfig : SyncConfig
{
    public TestSyncConfig()
    {
        GCOnStateSyncFinished = false;
        MultiSyncModeSelectorLoopTimerMs = 1;
        SyncDispatcherEmptyRequestDelayMs = 1;
        SyncDispatcherAllocateTimeoutMs = 1;
    }
}
