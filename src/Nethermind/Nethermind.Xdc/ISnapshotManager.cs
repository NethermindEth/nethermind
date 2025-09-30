// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Nethermind.Xdc;
public interface ISnapshotManager
{
    Snapshot? GetSnapshot(Hash256 hash);
    void StoreSnapshot(Snapshot snapshot);

    Snapshot? GetSnapshotByGapNumber(ulong gapNumber);
    Snapshot? GetSnapshotByHeaderNumber(ulong number, ulong xdcEpoch, ulong xdcGap);
    Snapshot? GetSnapshotByHeader(XdcBlockHeader? header);
    Address[] CalculateNextEpochMasternodes(XdcBlockHeader xdcHeader);
    Address[] GetMasternodes(XdcBlockHeader xdcHeader);
    Address[] GetPenalties(XdcBlockHeader xdcHeader);
}
