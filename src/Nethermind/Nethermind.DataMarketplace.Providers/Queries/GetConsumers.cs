// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Queries
{
    public class GetConsumers : PagedQueryBase
    {
        public Keccak? AssetId { get; set; }
        public Address? Address { get; set; }
        public bool OnlyWithAvailableUnits { get; set; }
    }
}
