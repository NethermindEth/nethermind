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

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class DataHeaderForRpc
    {
        public Keccak Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public BigInteger UnitPrice { get; set; }
        public string UnitType { get; set; }
        public string QueryType { get; set; }
        public uint MinUnits { get; set; }
        public uint MaxUnits { get; set; }
        public DataHeaderRulesForRpc Rules { get; set; }
        public DataHeaderProviderForRpc Provider { get; set; }
        public string File { get; set; }
        public byte[] Data { get; set; }
        public string State { get; }
        public string TermsAndConditions { get; set; }
        public bool KycRequired { get; set; }
        public string Plugin { get; set; }

        public DataHeaderForRpc()
        {
        }

        public DataHeaderForRpc(DataHeader dataHeader)
        {
            Id = dataHeader.Id;
            Name = dataHeader.Name;
            Description = dataHeader.Description;
            UnitPrice = dataHeader.UnitPrice;
            UnitType = dataHeader.UnitType.ToString().ToLowerInvariant();
            QueryType = dataHeader.QueryType.ToString().ToLowerInvariant();
            MinUnits = dataHeader.MinUnits;
            MaxUnits = dataHeader.MaxUnits;
            Rules = new DataHeaderRulesForRpc(dataHeader.Rules);
            Provider = new DataHeaderProviderForRpc(dataHeader.Provider);
            File = dataHeader.File;
            State = dataHeader.State.ToString().ToLowerInvariant();
            TermsAndConditions = dataHeader.TermsAndConditions;
            KycRequired = dataHeader.KycRequired;
            Plugin = dataHeader.Plugin;
        }
    }
}