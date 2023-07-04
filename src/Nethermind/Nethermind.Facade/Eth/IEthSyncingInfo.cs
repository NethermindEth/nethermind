// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Facade.Eth
{
    public interface IEthSyncingInfo
    {
        SyncingResult GetFullInfo();

        bool IsSyncing();

        TimeSpan UpdateAndGetSyncTime();

        SyncMode SyncMode { get; }
    }
}
