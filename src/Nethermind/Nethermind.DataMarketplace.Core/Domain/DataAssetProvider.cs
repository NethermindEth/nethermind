// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataAssetProvider
    {
        public Address Address { get; }
        public string Name { get; }

        public DataAssetProvider(Address address, string name)
        {
            Address = address ?? throw new ArgumentException("Invalid data asset provider address.", nameof(address));
            Name = string.IsNullOrWhiteSpace(name)
                ? throw new ArgumentException("Invalid data asset provider name.", nameof(name))
                : name;
        }
    }
}
