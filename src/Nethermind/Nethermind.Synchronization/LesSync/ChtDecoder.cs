// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            return Rlp.LengthOfSequence(
                Rlp.LengthOf(hash) + Rlp.LengthOf(totalDifficulty));
        }
    }
}
