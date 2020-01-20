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
using Nethermind.Core.Serialization;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DepositDecoder : IRlpDecoder<Deposit>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        private DepositDecoder()
        {
        }

        static DepositDecoder()
        {
            Nethermind.Core.Serialization.Rlp.Decoders[typeof(Deposit)] = new DepositDecoder();
        }

        public Deposit Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var id = rlpStream.DecodeKeccak();
            var units = rlpStream.DecodeUInt();
            var expiryTime = rlpStream.DecodeUInt();
            var value = rlpStream.DecodeUInt256();

            return new Deposit(id, units, expiryTime, value);
        }

        public Nethermind.Core.Serialization.Rlp Encode(Deposit item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Serialization.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Serialization.Rlp.Encode(
                Nethermind.Core.Serialization.Rlp.Encode(item.Id),
                Nethermind.Core.Serialization.Rlp.Encode(item.Units),
                Nethermind.Core.Serialization.Rlp.Encode(item.ExpiryTime),
                Nethermind.Core.Serialization.Rlp.Encode(item.Value));
        }

        public void Encode(MemoryStream stream, Deposit item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(Deposit item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}