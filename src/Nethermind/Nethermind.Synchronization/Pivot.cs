// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Synchronization;

public class Pivot : IPivot
{
    private readonly ISyncConfig _syncconfig;

    public Pivot(ISyncConfig syncConfig)
    {
        _syncconfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));

        PivotNumber = _syncconfig.PivotNumberParsed;
        PivotHash = _syncconfig.PivotHashParsed;
        PivotTotalDifficulty = _syncconfig.PivotTotalDifficultyParsed;
        PivotDestinationNumber = 0L;
    }

    public long PivotNumber { get; }

    public Hash256? PivotHash { get; }

    public Hash256? PivotParentHash => null;

    public UInt256? PivotTotalDifficulty { get; }

    public long PivotDestinationNumber { get; }
}
