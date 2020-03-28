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
using Nethermind.Dirichlet.Numerics;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class EthRequestDecoder : IRlpDecoder<EthRequest>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static EthRequestDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(EthRequest)] = new EthRequestDecoder();
        }

        public EthRequest Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            try
            {
                rlpStream.ReadSequenceLength();
                Keccak id = rlpStream.DecodeKeccak();
                string host = rlpStream.DecodeString();
                Address address = rlpStream.DecodeAddress();
                UInt256 value = rlpStream.DecodeUInt256();
                DateTime requestedAt = DateTimeOffset.FromUnixTimeSeconds(rlpStream.DecodeLong()).UtcDateTime;
                Keccak transactionHash = rlpStream.DecodeKeccak();

                return new EthRequest(id, host, address, value, requestedAt, transactionHash);
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(EthRequest)} cannot be deserialized from", e);
            }
        }

        public Serialization.Rlp.Rlp Encode(EthRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.Host),
                Serialization.Rlp.Rlp.Encode(item.Address),
                Serialization.Rlp.Rlp.Encode(item.Value),
                Serialization.Rlp.Rlp.Encode(new DateTimeOffset(item.RequestedAt).ToUnixTimeSeconds()),
                Serialization.Rlp.Rlp.Encode(item.TransactionHash));
        }

        public int GetLength(EthRequest item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}