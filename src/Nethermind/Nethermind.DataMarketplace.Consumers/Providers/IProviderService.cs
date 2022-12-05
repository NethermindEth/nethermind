// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Providers.Domain;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Providers
{
    public interface IProviderService
    {
        INdmPeer? GetPeer(Address address);
        IEnumerable<INdmPeer> GetPeers();
        Task<IReadOnlyList<ProviderInfo>> GetKnownAsync();
        void Add(INdmPeer peer);
        void Remove(PublicKey nodeId);
        Task ChangeAddressAsync(INdmPeer peer, Address address);
    }
}
