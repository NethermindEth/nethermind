// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.Providers.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class ProviderInfoForRpc
    {
        public string Name { get; }
        public Address Address { get; }

        public ProviderInfoForRpc(ProviderInfo provider)
        {
            Name = provider.Name;
            Address = provider.Address;
        }
    }
}
