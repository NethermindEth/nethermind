/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataHeader
    {
        public Keccak Id { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public UInt256 UnitPrice { get; private set; }
        public DataHeaderUnitType UnitType { get; private set; }
        public QueryType QueryType { get; private set; }
        public uint MinUnits { get; private set; }
        public uint MaxUnits { get; private set; }
        public DataHeaderRules Rules { get; private set; }
        public DataHeaderProvider Provider { get; private set; }
        public string File { get; private set; }
        public DataHeaderState State { get; private set; }
        public string TermsAndConditions { get; private set; }
        public bool KycRequired { get; private set; }
        public string Plugin { get; private set; }

        public DataHeader(Keccak id, string name, string description, UInt256 unitPrice,
            DataHeaderUnitType unitType, uint minUnits, uint maxUnits, DataHeaderRules rules,
            DataHeaderProvider provider, string file = null, QueryType queryType = QueryType.Stream,
            DataHeaderState state = DataHeaderState.Unpublished, string termsAndConditions = null,
            bool kycRequired = false, string plugin = null)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.Name) || provider.Address == null)
            {
                throw new ArgumentException("Invalid data header provider.", nameof(provider));
            }

            if (id == Keccak.Zero)
            {
                throw new ArgumentException("Invalid data header id.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            {
                throw new ArgumentException("Invalid data header name.", nameof(name));
            }

            if (string.IsNullOrWhiteSpace(description) || description.Length > 256)
            {
                throw new ArgumentException("Invalid data header description.", nameof(description));
            }
            
            if (termsAndConditions?.Length > 10000)
            {
                throw new ArgumentException("Invalid terms and conditions (over 10000 chars).", nameof(description));
            }

            if (rules is null)
            {
                throw new ArgumentException($"Missing rules.", nameof(rules));
            }

            if (rules.Expiry is null && rules.Expiry.Value <= 0)
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
            Plugin = plugin?.ToLowerInvariant();
        }

        public void ChangeState(DataHeaderState state)
        {
            if (State == state || State == DataHeaderState.Archived)
            {
                return;
            }

            State = state;
        }

        public void ClearPlugin() => Plugin = string.Empty;
    }
}