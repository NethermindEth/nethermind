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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class FaucetRequestDetailsDecoder : IRlpNdmDecoder<FaucetRequestDetails>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static FaucetRequestDetailsDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(FaucetRequestDetails)] = new FaucetRequestDetailsDecoder();
        }

        public FaucetRequestDetails Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return FaucetRequestDetails.Empty;
            }
            
            string host = rlpStream.DecodeString();
            Address address = rlpStream.DecodeAddress();
            UInt256 value = rlpStream.DecodeUInt256();
            DateTime date = DateTimeOffset.FromUnixTimeSeconds(rlpStream.DecodeLong()).UtcDateTime;
            Keccak transactionHash = rlpStream.DecodeKeccak();

            return new FaucetRequestDetails(host, address, value, date, transactionHash);
        }

        public void Encode(RlpStream stream, FaucetRequestDetails item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(FaucetRequestDetails item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }
            
            long date = (item.Date == DateTime.MinValue || item.Date == null) ? 0 : new DateTimeOffset(item.Date.Value).ToUnixTimeSeconds();

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Host),
                Serialization.Rlp.Rlp.Encode(item.Address),
                Serialization.Rlp.Rlp.Encode(item.Value),
                Serialization.Rlp.Rlp.Encode(date),
                Serialization.Rlp.Rlp.Encode(item.TransactionHash));
        }

        public int GetLength(FaucetRequestDetails item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
