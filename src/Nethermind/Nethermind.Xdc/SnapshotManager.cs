// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class SnapshotManager : ISnapshotManager
{
    public Address[] CalculateNextEpochMasternodes(XdcBlockHeader xdcHeader)
    {
        throw new NotImplementedException();
    }

    public Address GetBlockSealer(BlockHeader header)
    {
        throw new NotImplementedException();
    }

    public ulong GetLastSignersCount()
    {
        throw new NotImplementedException();
    }

    public Address[] GetPenalties(XdcBlockHeader xdcHeader)
    {
        throw new NotImplementedException();
    }

    public bool HasSignedRecently(Snapshot snapshot, long number, Address signer)
    {
        throw new NotImplementedException();
    }

    public bool IsInTurn(Snapshot snapshot, long number, Address signer)
    {
        throw new NotImplementedException();
    }

    public bool IsValidVote(Snapshot snapshot, Address address, bool authorize)
    {
        throw new NotImplementedException();
    }

    public void TryCacheSnapshot(Snapshot snapshot)
    {
        throw new NotImplementedException();
    }

    public bool TryGetSnapshot(long number, Hash256 hash, out Snapshot snapshot)
    {
        throw new NotImplementedException();
    }

    public bool TryGetSnapshot(XdcBlockHeader header, out Snapshot snapshot)
    {
        throw new NotImplementedException();
    }

    public bool TryGetSnapshot(ulong gapNumber, bool isGapNumber, out Snapshot snap)
    {
        throw new NotImplementedException();
    }

    public bool TryStoreSnapshot(Snapshot snapshot)
    {
        throw new NotImplementedException();
    }

    internal Address[] GetMasternodes(XdcBlockHeader xdcHeader)
    {
        throw new NotImplementedException();
    }

    Address[] ISnapshotManager.GetMasternodes(XdcBlockHeader xdcHeader)
    {
        return GetMasternodes(xdcHeader);
    }
}
