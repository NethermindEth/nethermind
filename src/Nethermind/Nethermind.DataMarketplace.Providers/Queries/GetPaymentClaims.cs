// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Queries
{
    public class GetPaymentClaims : PagedQueryBase
    {
        public Keccak? DepositId { get; set; }
        public Keccak? AssetId { get; set; }
        public Address? Consumer { get; set; }
        public bool OnlyUnclaimed { get; set; }
        public bool OnlyPending { get; set; }
    }
}
