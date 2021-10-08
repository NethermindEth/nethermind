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

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DepositDecoder : IRlpNdmDecoder<Deposit>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DepositDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(Deposit)] = new DepositDecoder();
        }

        public Deposit Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            Keccak id = rlpStream.DecodeKeccak();
            uint units = rlpStream.DecodeUInt();
            uint expiryTime = rlpStream.DecodeUInt();
            UInt256 value = rlpStream.DecodeUInt256();

            return new Deposit(id, units, expiryTime, value);
        }

        public void Encode(RlpStream stream, Deposit item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(Deposit item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.Units),
                Serialization.Rlp.Rlp.Encode(item.ExpiryTime),
                Serialization.Rlp.Rlp.Encode(item.Value));
        }

        public int GetLength(Deposit item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
