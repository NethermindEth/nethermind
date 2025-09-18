// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal interface ISnapshotManager
{
    bool TryGetSnapshot(Hash256 hash, out Snapshot snapshot);
    bool TryStoreSnapshot(Snapshot snapshot);
    bool TryGetSnapshot(XdcBlockHeader header, out Snapshot snapshot);
    void TryCacheSnapshot(Snapshot snapshot);
    bool TryGetSnapshot(ulong gapNumber, bool isGapNumber, out Snapshot snap);
}
