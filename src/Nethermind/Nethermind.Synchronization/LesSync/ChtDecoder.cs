//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Synchronization.LesSync
{
    public class ChtDecoder : IRlpDecoder<(Keccak?, UInt256)>
    {
        public (Keccak?, UInt256) Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return (null, 0);
            }

            rlpStream.ReadSequenceLength();
            Keccak hash = rlpStream.DecodeKeccak();
            UInt256 totalDifficulty = rlpStream.DecodeUInt256();
            return (hash, totalDifficulty);
        }

        public (Keccak?, UInt256) Decode(byte[] bytes)
        {
            return Decode(new RlpStream(bytes));
        }

        public Rlp Encode((Keccak?, UInt256) item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (Keccak? hash, UInt256 totalDifficulty) = item;
            return Rlp.Encode(
                Rlp.Encode(hash),
                Rlp.Encode(totalDifficulty));
        }

        public int GetLength((Keccak?, UInt256) item, RlpBehaviors rlpBehaviors)
        {
            (Keccak? hash, UInt256 totalDifficulty) = item;
            return Rlp.GetSequenceRlpLength(
                Rlp.LengthOf(hash) + Rlp.LengthOf(totalDifficulty));
        }
    }
}
