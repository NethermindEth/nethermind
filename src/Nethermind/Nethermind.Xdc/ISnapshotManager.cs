// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Nethermind.Xdc;

public interface ISnapshotManager
{
    ImmutableSortedSet<Address> CalculateNextEpochMasternodes(XdcBlockHeader xdcHeader);
    ImmutableSortedSet<Address> GetMasternodes(XdcBlockHeader xdcHeader);
    ImmutableSortedSet<Address> GetPenalties(XdcBlockHeader xdcHeader);
}
