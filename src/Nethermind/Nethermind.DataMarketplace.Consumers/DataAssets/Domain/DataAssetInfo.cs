// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Consumers.DataAssets.Domain
{
    public class DataAssetInfo
    {
        public Keccak Id { get; }
        public string Name { get; }
        public string Description { get; }

        public DataAssetInfo(Keccak id, string name, string description)
        {
            Id = id;
            Name = name;
            Description = description;
        }
    }
}
