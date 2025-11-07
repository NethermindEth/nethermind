// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
/// <summary>
/// Skeleton implementation
/// </summary>
internal class SyncInfoManager(IXdcConsensusContext xdcConsensusContext) : ISyncInfoManager
{
    public SyncInfo GetSyncInfo() => new SyncInfo(xdcConsensusContext.HighestQC, xdcConsensusContext.HighestTC);

    public void ProcessSyncInfo(SyncInfo syncInfo)
    {
        
    }

    public bool VerifySyncInfo(SyncInfo syncInfo)
    {
        return true;
    }
}
