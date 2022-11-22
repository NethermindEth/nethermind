// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class DataAssetForRpc
    {
        public Keccak? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public UInt256? UnitPrice { get; set; }
        public string? UnitType { get; set; }
        public string? QueryType { get; set; }
        public uint MinUnits { get; set; }
        public uint MaxUnits { get; set; }
        public DataAssetRulesForRpc? Rules { get; set; }
        public DataAssetProviderForRpc? Provider { get; set; }
        public string? File { get; set; }
        public byte[]? Data { get; set; }
        public string? State { get; }
        public string? TermsAndConditions { get; set; }
        public bool? KycRequired { get; set; }
        public string? Plugin { get; set; }

        public DataAssetForRpc()
        {
        }

        public DataAssetForRpc(DataAsset dataAsset)
        {
            Id = dataAsset.Id;
            Name = dataAsset.Name;
            Description = dataAsset.Description;
            UnitPrice = dataAsset.UnitPrice;
            UnitType = dataAsset.UnitType.ToString().ToLowerInvariant();
            QueryType = dataAsset.QueryType.ToString().ToLowerInvariant();
            MinUnits = dataAsset.MinUnits;
            MaxUnits = dataAsset.MaxUnits;
            Rules = new DataAssetRulesForRpc(dataAsset.Rules);
            Provider = new DataAssetProviderForRpc(dataAsset.Provider);
            File = dataAsset.File;
            State = dataAsset.State.ToString().ToLowerInvariant();
            TermsAndConditions = dataAsset.TermsAndConditions;
            KycRequired = dataAsset.KycRequired;
            Plugin = dataAsset.Plugin;
        }
    }
}
