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

using System.IO;
using Nethermind.Core.Encoding;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataAssetDecoder : IRlpDecoder<DataAsset>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DataAssetDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(DataAsset)] = new DataAssetDecoder();
        }

        public DataAsset Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var id = rlpStream.DecodeKeccak();
            var name = rlpStream.DecodeString();
            var description = rlpStream.DecodeString();
            var unitPrice = rlpStream.DecodeUInt256();
            var unitType = (DataAssetUnitType) rlpStream.DecodeInt();
            var minUnits = rlpStream.DecodeUInt();
            var maxUnits = rlpStream.DecodeUInt();
            var rules = Nethermind.Core.Encoding.Rlp.Decode<DataAssetRules>(rlpStream);
            var provider = Nethermind.Core.Encoding.Rlp.Decode<DataAssetProvider>(rlpStream);
            var file = rlpStream.DecodeString();
            var queryType = (QueryType) rlpStream.DecodeInt();
            var state = (DataAssetState) rlpStream.DecodeInt();
            var termsAndConditions = rlpStream.DecodeString();
            var kycRequired = rlpStream.DecodeBool();
            var plugin = rlpStream.DecodeString();

            return new DataAsset(id, name, description, unitPrice, unitType, minUnits, maxUnits,
                rules, provider, file, queryType, state, termsAndConditions, kycRequired, plugin);
        }

        public Nethermind.Core.Encoding.Rlp Encode(DataAsset item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.Id),
                Nethermind.Core.Encoding.Rlp.Encode(item.Name),
                Nethermind.Core.Encoding.Rlp.Encode(item.Description),
                Nethermind.Core.Encoding.Rlp.Encode(item.UnitPrice),
                Nethermind.Core.Encoding.Rlp.Encode((int) item.UnitType),
                Nethermind.Core.Encoding.Rlp.Encode(item.MinUnits),
                Nethermind.Core.Encoding.Rlp.Encode(item.MaxUnits),
                Nethermind.Core.Encoding.Rlp.Encode(item.Rules),
                Nethermind.Core.Encoding.Rlp.Encode(item.Provider),
                Nethermind.Core.Encoding.Rlp.Encode(item.File),
                Nethermind.Core.Encoding.Rlp.Encode((int) item.QueryType),
                Nethermind.Core.Encoding.Rlp.Encode((int) item.State),
                Nethermind.Core.Encoding.Rlp.Encode(item.TermsAndConditions),
                Nethermind.Core.Encoding.Rlp.Encode(item.KycRequired),
                Nethermind.Core.Encoding.Rlp.Encode(item.Plugin));
        }

        public void Encode(MemoryStream stream, DataAsset item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(DataAsset item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}