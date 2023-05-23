// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Synchronization.LesSync
{
    public class ChtDecoder : IRlpStreamDecoder<(Keccak?, UInt256)>
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

        public void Encode(RlpStream stream, (Keccak?, UInt256) item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (Keccak? hash, UInt256 totalDifficulty) = item;
            int contentLength = GetContentLength(item, RlpBehaviors.None);
            stream.StartSequence(contentLength);
            stream.Encode(hash);
            stream.Encode(totalDifficulty);
        }

        public (Keccak?, UInt256) Decode(byte[] bytes)
        {
            return Decode(new RlpStream(bytes));
        }

        public Rlp Encode((Keccak?, UInt256) item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public int GetLength((Keccak?, UInt256) item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }

        private int GetContentLength((Keccak?, UInt256) item, RlpBehaviors rlpBehaviors)
        {
            (Keccak? hash, UInt256 totalDifficulty) = item;
            return Rlp.LengthOf(hash) + Rlp.LengthOf(totalDifficulty);
        }
    }
}
