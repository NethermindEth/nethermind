// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.DataMarketplace.Consumers.Providers.Domain
{
    public class ProviderInfo
    {
        public string Name { get; }
        public Address Address { get; }

        public ProviderInfo(string name, Address address)
        {
            Name = name;
            Address = address;
        }
    }
}
