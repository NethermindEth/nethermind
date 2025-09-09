// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class SnapshotManager : ISnapshotManager
{
    public ImmutableSortedSet<Address> CalculateNextEpochMasternodes(XdcBlockHeader xdcHeader)
    {
        throw new NotImplementedException();
    }

    public ImmutableSortedSet<Address> GetPenalties(XdcBlockHeader xdcHeader)
    {
        throw new NotImplementedException();
    }

    internal ImmutableSortedSet<Address> GetMasternodes(XdcBlockHeader xdcHeader)
    {
        throw new NotImplementedException();
    }

    ImmutableSortedSet<Address> ISnapshotManager.GetMasternodes(XdcBlockHeader xdcHeader)
    {
        return GetMasternodes(xdcHeader);
    }
}
