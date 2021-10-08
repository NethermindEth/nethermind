//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataAsset
    {
        public Keccak Id { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public UInt256 UnitPrice { get; private set; }
        public DataAssetUnitType UnitType { get; private set; }
        public QueryType QueryType { get; private set; }
        public uint MinUnits { get; private set; }
        public uint MaxUnits { get; private set; }
        public DataAssetRules Rules { get; private set; }
        public DataAssetProvider Provider { get; private set; }
        public string? File { get; private set; }
        public DataAssetState State { get; private set; }
        public string? TermsAndConditions { get; private set; }
        public bool KycRequired { get; private set; }
        public string? Plugin { get; private set; }

        public DataAsset(
            Keccak id,
            string name,
            string description,
            UInt256 unitPrice,
            DataAssetUnitType unitType,
            uint minUnits,
            uint maxUnits,
            DataAssetRules rules,
            DataAssetProvider provider,
            string? file = null,
            QueryType queryType = QueryType.Stream,
            DataAssetState state = DataAssetState.Unpublished,
            string? termsAndConditions = null,
            bool kycRequired = false,
            string? plugin = null)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.Name) || provider.Address == null)
            {
                throw new ArgumentException("Invalid data asset provider.", nameof(provider));
            }

            if (id == Keccak.Zero)
            {
                throw new ArgumentException("Invalid data asset id.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            {
                throw new ArgumentException("Invalid data asset name.", nameof(name));
            }

            if (string.IsNullOrWhiteSpace(description) || description.Length > 256)
            {
                throw new ArgumentException("Invalid data asset description.", nameof(description));
            }

            if (termsAndConditions?.Length > 10000)
            {
                throw new ArgumentException("Invalid terms and conditions (over 10000 chars).", nameof(description));
            }

            if (rules is null)
            {
                throw new ArgumentException($"Missing rules.", nameof(rules));
            }

            if (rules.Expiry != null && rules.Expiry.Value <= 0)
            {
                throw new ArgumentException($"Invalid expiry rule value: {rules.Expiry}.", nameof(rules.Expiry));
            }

            if (!(rules.UpfrontPayment is null) && rules.UpfrontPayment.Value > minUnits)
            {
                throw new ArgumentException($"Min units ({minUnits}) cannot be lower than upfront payment value" +
                                            $" ({rules.UpfrontPayment.Value}).", nameof(minUnits));
            }

            Provider = provider;
            State = state;
            Id = id;
            Name = name;
            Description = description;
            UnitPrice = unitPrice;
            UnitType = unitType;
            QueryType = queryType;
            MinUnits = minUnits;
            MaxUnits = maxUnits;
            Rules = rules;
            Provider = provider;
            File = file;
            State = state;
            TermsAndConditions = termsAndConditions;
            KycRequired = kycRequired;
            SetState(state);
            if (plugin != null)
            {
                SetPlugin(plugin);
            }
        }

        public void SetState(DataAssetState state)
        {
            if (State == state || State == DataAssetState.Archived)
            {
                return;
            }

            State = state;
        }

        public void ClearPlugin() => SetPlugin(string.Empty);

        public void SetPlugin(string? plugin)
        {
            string? pluginName = plugin?.ToLowerInvariant();
            if (Plugin == pluginName)
            {
                return;
            }

            Plugin = pluginName;
        }
    }
}