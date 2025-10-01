// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Spec;
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
    (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(XdcBlockHeader header, IXdcReleaseSpec spec);
}
