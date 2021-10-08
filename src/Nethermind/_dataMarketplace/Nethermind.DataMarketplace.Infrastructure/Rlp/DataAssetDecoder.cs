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
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataAssetDecoder : IRlpNdmDecoder<DataAsset>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DataAssetDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DataAsset)] = new DataAssetDecoder();
        }

        public DataAsset Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            try
            {
                rlpStream.ReadSequenceLength();
                Keccak id = rlpStream.DecodeKeccak();
                string name = rlpStream.DecodeString();
                string description = rlpStream.DecodeString();
                UInt256 unitPrice = rlpStream.DecodeUInt256();
                DataAssetUnitType unitType = (DataAssetUnitType) rlpStream.DecodeInt();
                uint minUnits = rlpStream.DecodeUInt();
                uint maxUnits = rlpStream.DecodeUInt();
                DataAssetRules rules = Serialization.Rlp.Rlp.Decode<DataAssetRules>(rlpStream);
                DataAssetProvider provider = Serialization.Rlp.Rlp.Decode<DataAssetProvider>(rlpStream);
                string file = rlpStream.DecodeString();
                QueryType queryType = (QueryType) rlpStream.DecodeInt();
                DataAssetState state = (DataAssetState) rlpStream.DecodeInt();
                string termsAndConditions = rlpStream.DecodeString();
                bool kycRequired = rlpStream.DecodeBool();
                string plugin = rlpStream.DecodeString();

                return new DataAsset(id, name, description, unitPrice, unitType, minUnits, maxUnits,
                    rules, provider, file, queryType, state, termsAndConditions, kycRequired, plugin);
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(DataAsset)} could not be deserialized", e);
            }
        }

        public void Encode(RlpStream stream, DataAsset item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DataAsset item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.Name),
                Serialization.Rlp.Rlp.Encode(item.Description),
                Serialization.Rlp.Rlp.Encode(item.UnitPrice),
                Serialization.Rlp.Rlp.Encode((int) item.UnitType),
                Serialization.Rlp.Rlp.Encode(item.MinUnits),
                Serialization.Rlp.Rlp.Encode(item.MaxUnits),
                Serialization.Rlp.Rlp.Encode(item.Rules),
                Serialization.Rlp.Rlp.Encode(item.Provider),
                Serialization.Rlp.Rlp.Encode(item.File),
                Serialization.Rlp.Rlp.Encode((int) item.QueryType),
                Serialization.Rlp.Rlp.Encode((int) item.State),
                Serialization.Rlp.Rlp.Encode(item.TermsAndConditions),
                Serialization.Rlp.Rlp.Encode(item.KycRequired),
                Serialization.Rlp.Rlp.Encode(item.Plugin));
        }

        public int GetLength(DataAsset item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
