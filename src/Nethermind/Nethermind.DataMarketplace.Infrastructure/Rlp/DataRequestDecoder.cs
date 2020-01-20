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

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Serialization;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataRequestDecoder : IRlpDecoder<DataRequest>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        private DataRequestDecoder()
        {
        }

        static DataRequestDecoder()
        {
            Nethermind.Core.Serialization.Rlp.Decoders[typeof(DataRequest)] = new DataRequestDecoder();
        }

        public DataRequest Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var assetId = rlpStream.DecodeKeccak();
            var units = rlpStream.DecodeUInt();
            var value = rlpStream.DecodeUInt256();
            var expiryTime = rlpStream.DecodeUInt();
            var salt = rlpStream.DecodeByteArray();
            var provider = rlpStream.DecodeAddress();
            var consumer = rlpStream.DecodeAddress();
            var signature = SignatureDecoder.DecodeSignature(rlpStream);

            return new DataRequest(assetId, units, value, expiryTime, salt, provider, consumer, signature);
        }

        public Nethermind.Core.Serialization.Rlp Encode(DataRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Serialization.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Serialization.Rlp.Encode(
                Nethermind.Core.Serialization.Rlp.Encode(item.DataAssetId),
                Nethermind.Core.Serialization.Rlp.Encode(item.Units),
                Nethermind.Core.Serialization.Rlp.Encode(item.Value),
                Nethermind.Core.Serialization.Rlp.Encode(item.ExpiryTime),
                Nethermind.Core.Serialization.Rlp.Encode(item.Pepper),
                Nethermind.Core.Serialization.Rlp.Encode(item.Provider),
                Nethermind.Core.Serialization.Rlp.Encode(item.Consumer),
                Nethermind.Core.Serialization.Rlp.Encode(item.Signature.V),
                Nethermind.Core.Serialization.Rlp.Encode(item.Signature.R.WithoutLeadingZeros()),
                Nethermind.Core.Serialization.Rlp.Encode(item.Signature.S.WithoutLeadingZeros()));
        }

        public void Encode(MemoryStream stream, DataRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(DataRequest item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}