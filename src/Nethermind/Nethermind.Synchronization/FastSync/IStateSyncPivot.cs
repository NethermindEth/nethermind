// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using ConcurrentCollections;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.FastSync;

public interface IStateSyncPivot
{
    BlockHeader? GetPivotHeader();
    void UpdateHeaderForcefully();
    ConcurrentHashSet<Hash256> UpdatedStorages { get; }
    long Diff { get; }
}
