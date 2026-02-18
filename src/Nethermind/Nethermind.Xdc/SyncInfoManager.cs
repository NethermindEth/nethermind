// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System.Collections.Generic;

namespace Nethermind.Xdc;
/// <summary>
/// Skeleton implementation
/// </summary>
internal class SyncInfoManager(IXdcConsensusContext xdcConsensusContext) : ISyncInfoManager
{
    public IDictionary<(ulong Round, Hash256 Hash), ArrayPoolList<SyncInfo>> GetReceivedSyncInfos()
    {
        throw new System.NotImplementedException();
    }

    public SyncInfo GetSyncInfo() => new SyncInfo(xdcConsensusContext.HighestQC, xdcConsensusContext.HighestTC);

    public void ProcessSyncInfo(SyncInfo syncInfo)
    {

    }

    public bool VerifySyncInfo(SyncInfo syncInfo)
    {
        return true;
    }
}
