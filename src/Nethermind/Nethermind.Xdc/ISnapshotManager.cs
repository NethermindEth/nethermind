// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Nethermind.Xdc;
internal interface ISnapshotManager
{
    ulong GetLastSignersCount();
    bool TryGetSnapshot(long number, Hash256 hash, out Snapshot snapshot);
    bool TryStoreSnapshot(Snapshot snapshot);
    Address GetBlockSealer(BlockHeader header);
    bool IsValidVote(Snapshot snapshot, Address address, bool authorize);
    bool IsInTurn(Snapshot snapshot, long number, Address signer);
    bool HasSignedRecently(Snapshot snapshot, long number, Address signer);
    bool TryGetSnapshot(XdcBlockHeader header, out Snapshot snapshot);
    void TryCacheSnapshot(Snapshot snapshot);
    bool TryGetSnapshot(ulong gapNumber, bool isGapNumber, out Snapshot snap);
    ImmutableSortedSet<Address> CalculateNextEpochMasternodes(XdcBlockHeader xdcHeader);
    ImmutableSortedSet<Address> GetMasternodes(XdcBlockHeader xdcHeader);
    ImmutableSortedSet<Address> GetPenalties(XdcBlockHeader xdcHeader);
}
