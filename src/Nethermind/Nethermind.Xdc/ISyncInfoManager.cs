// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System.Collections.Generic;

namespace Nethermind.Xdc;

public interface ISyncInfoManager
{
    void ProcessSyncInfo(SyncInfo syncInfo);
    bool VerifySyncInfo(SyncInfo syncInfo);
    SyncInfo GetSyncInfo();

    IDictionary<(ulong Round, Hash256 Hash), ArrayPoolList<SyncInfo>> GetReceivedSyncInfos();
}
