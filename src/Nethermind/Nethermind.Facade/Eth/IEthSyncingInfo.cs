// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Facade.Eth
{
    public interface IEthSyncingInfo
    {
        SyncingResult GetFullInfo();

        bool IsSyncing();

        TimeSpan UpdateAndGetSyncTime();
    }
}
