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
using System.IO;
using Nethermind.Core.Serialization;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class FaucetRequestDetailsDecoder : IRlpDecoder<FaucetRequestDetails>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        public FaucetRequestDetailsDecoder()
        {
        }

        static FaucetRequestDetailsDecoder()
        {
            Nethermind.Core.Serialization.Rlp.Decoders[typeof(FaucetRequestDetails)] = new FaucetRequestDetailsDecoder();
        }

        public FaucetRequestDetails Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var host = rlpStream.DecodeString();
            var address = rlpStream.DecodeAddress();
            var value = rlpStream.DecodeUInt256();
            var date = DateTimeOffset.FromUnixTimeSeconds(rlpStream.DecodeLong()).UtcDateTime;
            var transactionHash = rlpStream.DecodeKeccak();

            return new FaucetRequestDetails(host, address, value, date, transactionHash);
        }

        public Nethermind.Core.Serialization.Rlp Encode(FaucetRequestDetails item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Serialization.Rlp.OfEmptySequence;
            }

            var date = item.Date == DateTime.MinValue ? 0 : new DateTimeOffset(item.Date).ToUnixTimeSeconds();

            return Nethermind.Core.Serialization.Rlp.Encode(
                Nethermind.Core.Serialization.Rlp.Encode(item.Host),
                Nethermind.Core.Serialization.Rlp.Encode(item.Address),
                Nethermind.Core.Serialization.Rlp.Encode(item.Value),
                Nethermind.Core.Serialization.Rlp.Encode(date),
                Nethermind.Core.Serialization.Rlp.Encode(item.TransactionHash));
        }

        public void Encode(MemoryStream stream, FaucetRequestDetails item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(FaucetRequestDetails item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}