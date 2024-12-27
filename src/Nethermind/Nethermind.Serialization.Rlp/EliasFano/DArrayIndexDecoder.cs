// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core.Collections.EliasFano;

namespace Nethermind.Serialization.Rlp.EliasFano;

public class DArrayIndexDecoder : IRlpStreamDecoder<DArrayIndex>
{
    public int GetLength(DArrayIndex item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }

    public DArrayIndex Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        rlpStream.ReadSequenceLength();
        return new DArrayIndex(
            MemoryMarshal.Cast<byte, int>(rlpStream.DecodeByteArraySpan()).ToArray(),
            MemoryMarshal.Cast<byte, ushort>(rlpStream.DecodeByteArraySpan()).ToArray(),
            MemoryMarshal.Cast<byte, int>(rlpStream.DecodeByteArraySpan()).ToArray(),
            rlpStream.DecodeInt(),
            rlpStream.DecodeBool()
        );
    }

    public void Encode(RlpStream stream, DArrayIndex item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);
        stream.Encode(MemoryMarshal.Cast<int, byte>(item._blockInventory));
        stream.Encode(MemoryMarshal.Cast<ushort, byte>(item._subBlockInventory));
        stream.Encode(MemoryMarshal.Cast<int, byte>(item._overflowPositions));
        stream.Encode(item.NumPositions);
        stream.Encode(item.OverOne);
    }

    public int GetContentLength(DArrayIndex item, RlpBehaviors rlpBehaviors)
    {
        int length = 0;
        length += Rlp.LengthOf(MemoryMarshal.Cast<int, byte>(item._blockInventory));
        length += Rlp.LengthOf(MemoryMarshal.Cast<ushort, byte>(item._subBlockInventory));
        length += Rlp.LengthOf(MemoryMarshal.Cast<int, byte>(item._overflowPositions));
        length += Rlp.LengthOf(item.NumPositions);
        length += Rlp.LengthOf(item.OverOne);
        return length;
    }
}
