// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Synchronization.LesSync
{
    public class ChtDecoder : IRlpStreamDecoder<(Hash256?, UInt256)>
    {
        public (Hash256?, UInt256) Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return (null, 0);
            }

            rlpStream.ReadSequenceLength();
            Hash256 hash = rlpStream.DecodeKeccak();
            UInt256 totalDifficulty = rlpStream.DecodeUInt256();
            return (hash, totalDifficulty);
        }

        public void Encode(RlpStream stream, (Hash256?, UInt256) item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (Hash256? hash, UInt256 totalDifficulty) = item;
            int contentLength = GetContentLength(item, RlpBehaviors.None);
            stream.StartSequence(contentLength);
            stream.Encode(hash);
            stream.Encode(totalDifficulty);
        }

        public (Hash256?, UInt256) Decode(byte[] bytes)
        {
            return Decode(new RlpStream(bytes));
        }

        public Rlp Encode((Hash256?, UInt256) item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public int GetLength((Hash256?, UInt256) item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }

        private static int GetContentLength((Hash256?, UInt256) item, RlpBehaviors rlpBehaviors)
        {
            (Hash256? hash, UInt256 totalDifficulty) = item;
            return Rlp.LengthOf(hash) + Rlp.LengthOf(totalDifficulty);
        }
    }
}
