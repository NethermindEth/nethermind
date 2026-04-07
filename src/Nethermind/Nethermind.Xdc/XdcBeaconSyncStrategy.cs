// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Synchronization;

namespace Nethermind.Xdc;

public class XdcBeaconSyncStrategy : IBeaconSyncStrategy
{
    private readonly ISyncConfig _syncConfig;

    public XdcBeaconSyncStrategy(ISyncConfig syncConfig)
    {
        _syncConfig = syncConfig;
    }

    public void AllowBeaconHeaderSync() { }

    public bool ShouldBeInBeaconHeaders() => false;

    public bool ShouldBeInBeaconModeControl() => false;

    public bool IsBeaconSyncFinished(BlockHeader? blockHeader) => true;

    public bool MergeTransitionFinished => false;

    public long? GetTargetBlockHeight() => _syncConfig.PivotNumber > 0 ? _syncConfig.PivotNumber : null;

    public Hash256? GetFinalizedHash() => null;

    public Hash256? GetHeadBlockHash() => null;
}

