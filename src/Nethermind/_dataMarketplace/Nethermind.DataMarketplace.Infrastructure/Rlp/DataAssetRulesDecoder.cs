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
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataAssetRulesDecoder : IRlpNdmDecoder<DataAssetRules>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }
        
        static DataAssetRulesDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DataAssetRules)] = new DataAssetRulesDecoder();
        }

        public DataAssetRules Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            try
            {
                rlpStream.ReadSequenceLength();
                DataAssetRule expiry = Serialization.Rlp.Rlp.Decode<DataAssetRule>(rlpStream);
                DataAssetRule upfrontPayment = Serialization.Rlp.Rlp.Decode<DataAssetRule>(rlpStream);

                return new DataAssetRules(expiry, upfrontPayment);
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(DataAssetRules)} could not be deserialized", e);
            }
        }

        public void Encode(RlpStream stream, DataAssetRules item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DataAssetRules item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Expiry),
                Serialization.Rlp.Rlp.Encode(item.UpfrontPayment));
        }

        public int GetLength(DataAssetRules item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
