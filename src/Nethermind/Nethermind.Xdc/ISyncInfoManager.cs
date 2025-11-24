// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

public interface ISyncInfoManager
{
    void ProcessSyncInfo(SyncInfo syncInfo);
    bool VerifySyncInfo(SyncInfo syncInfo);
    SyncInfo GetSyncInfo();
}
