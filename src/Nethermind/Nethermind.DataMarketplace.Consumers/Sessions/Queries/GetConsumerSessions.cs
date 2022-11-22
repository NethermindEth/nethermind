// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Sessions.Queries
{
    public class GetConsumerSessions : PagedQueryBase
    {
        public Keccak? DepositId { get; set; }
        public Keccak? DataAssetId { get; set; }
        public PublicKey? ConsumerNodeId { get; set; }
        public Address? ConsumerAddress { get; set; }
        public PublicKey? ProviderNodeId { get; set; }
        public Address? ProviderAddress { get; set; }
    }
}
