// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections.EliasFano;

namespace Nethermind.Serialization.Rlp.EliasFano;

public class EliasFanoDecoder: IRlpStreamDecoder<Core.Collections.EliasFano.EliasFano>
{
    private readonly BitVectorDecoder _vecDecoder = new();
    private readonly DArrayDecoder _dArrayDecoder = new();

    public int GetLength(Core.Collections.EliasFano.EliasFano item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }

    public int GetContentLength(Core.Collections.EliasFano.EliasFano item, RlpBehaviors rlpBehaviors)
    {
        int contentLength = 0;
        contentLength += _dArrayDecoder.GetLength(item._highBits, rlpBehaviors);
        contentLength += _vecDecoder.GetLength(item._lowBits, rlpBehaviors);
        contentLength += Rlp.LengthOf(item._lowLen);
        contentLength += Rlp.LengthOf(item._universe);
        return contentLength;
    }

    public Core.Collections.EliasFano.EliasFano Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        rlpStream.ReadSequenceLength();
        DArray hghBits = _dArrayDecoder.Decode(rlpStream);
        BitVector lowBits = _vecDecoder.Decode(rlpStream);
        int lowLen = rlpStream.DecodeInt();
        ulong universe = rlpStream.DecodeUlong();
        return new Core.Collections.EliasFano.EliasFano(hghBits, lowBits, lowLen, universe);
    }

    public void Encode(RlpStream stream, Core.Collections.EliasFano.EliasFano item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);
        _dArrayDecoder.Encode(stream, item._highBits, rlpBehaviors);
        _vecDecoder.Encode(stream, item._lowBits, rlpBehaviors);
        stream.Encode(item._lowLen);
        stream.Encode(item._universe);
    }
}
