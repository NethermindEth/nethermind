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
    bool TryGetSnapshot(Hash256 hash, out Snapshot snapshot);
    bool TryStoreSnapshot(Snapshot snapshot);
    bool TryCacheSnapshot(Snapshot snapshot);
    bool TryGetSnapshotByGapNumber(ulong gapNumber, out Snapshot snap);
    bool TryGetSnapshotByHeaderNumber(ulong gapNumber, out Snapshot snap);
    bool TryGetSnapshotByHeader(XdcBlockHeader? header, out Snapshot snapshot);
    Address[] CalculateNextEpochMasternodes(XdcBlockHeader xdcHeader);
    Address[] GetMasternodes(XdcBlockHeader xdcHeader);
    Address[] GetPenalties(XdcBlockHeader xdcHeader);
}
