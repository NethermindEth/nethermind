// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class XdcSealValidator(ISnapshotManager snapshotManager) : ISealValidator
{
    public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
    {
        throw new NotImplementedException();
    }

    public bool ValidateSeal(BlockHeader header, bool force)
    {
        if (header is not XdcBlockHeader xdcHeader)
            throw new ArgumentException($"Only type of {nameof(XdcBlockHeader)} is allowed, but got type {header.GetType().Name}.", nameof(header));
        if (header.Author is null)
            return false;

        ImmutableSortedSet<Address> masternodes = xdcHeader.ValidatorsAddress == null ? snapshotManager.GetMasternodes(xdcHeader) : xdcHeader.ValidatorsAddress; 

        if (!masternodes.Contains(header.Author))
            return false;
        return true;
    }
}
