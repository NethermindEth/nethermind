// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Queries
{
    public class GetConsumerDepositApprovals : PagedQueryBase
    {
        public Keccak? DataAssetId { get; set; }
        public Address? Provider { get; set; }
        public bool OnlyPending { get; set; }
    }
}
