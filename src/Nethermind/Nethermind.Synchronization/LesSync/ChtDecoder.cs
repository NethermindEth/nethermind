// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Synchronization.LesSync
{
    public class ChtDecoder : IRlpStreamDecoder<(Commitment?, UInt256)>
    {
        public (Commitment?, UInt256) Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return (null, 0);
            }

            rlpStream.ReadSequenceLength();
            Commitment hash = rlpStream.DecodeKeccak();
            UInt256 totalDifficulty = rlpStream.DecodeUInt256();
            return (hash, totalDifficulty);
        }

        public void Encode(RlpStream stream, (Commitment?, UInt256) item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (Commitment? hash, UInt256 totalDifficulty) = item;
            int contentLength = GetContentLength(item, RlpBehaviors.None);
            stream.StartSequence(contentLength);
            stream.Encode(hash);
            stream.Encode(totalDifficulty);
        }

        public (Commitment?, UInt256) Decode(byte[] bytes)
        {
            return Decode(new RlpStream(bytes));
        }

        public Rlp Encode((Commitment?, UInt256) item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public int GetLength((Commitment?, UInt256) item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }

        private int GetContentLength((Commitment?, UInt256) item, RlpBehaviors rlpBehaviors)
        {
            (Commitment? hash, UInt256 totalDifficulty) = item;
            return Rlp.LengthOf(hash) + Rlp.LengthOf(totalDifficulty);
        }
    }
}
