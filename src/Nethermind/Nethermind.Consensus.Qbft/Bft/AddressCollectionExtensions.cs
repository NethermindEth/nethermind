// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Bft;

public static class AddressCollectionExtensions
{
    /// <summary>Linear membership test; validator sets are small so no set is built.</summary>
    public static bool ContainsAddress(this IReadOnlyCollection<Address> addresses, Address address)
    {
        foreach (Address candidate in addresses)
        {
            if (candidate == address) return true;
        }

        return false;
    }
}
