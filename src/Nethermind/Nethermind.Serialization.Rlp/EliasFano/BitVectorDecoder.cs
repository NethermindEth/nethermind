// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections.EliasFano;

namespace Nethermind.Serialization.Rlp.EliasFano;

public class BitVectorDecoder : IRlpStreamDecoder<BitVector>
{
    public int GetLength(BitVector item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }

    public BitVector Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        rlpStream.ReadSequenceLength();
        int bitVecLength = rlpStream.DecodeInt();
        List<ulong> bitVecWords =
            MemoryMarshal.Cast<byte, ulong>(rlpStream.DecodeByteArraySpan()).ToArray().ToList();
        return new BitVector(bitVecWords, bitVecLength);
    }

    public void Encode(RlpStream stream, BitVector item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Length);
        stream.Encode(MemoryMarshal.Cast<ulong, byte>(CollectionsMarshal.AsSpan(item.Words)));
    }

    public int GetContentLength(BitVector item, RlpBehaviors rlpBehaviors)
    {
        int length = 0;
        length += Rlp.LengthOf(item.Length);
        length += Rlp.LengthOf(MemoryMarshal.Cast<ulong, byte>(CollectionsMarshal.AsSpan(item.Words)));
        return length;
    }
}
