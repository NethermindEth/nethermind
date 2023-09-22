// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections.EliasFano;

namespace Nethermind.Serialization.Rlp.EliasFano;

public class DArrayDecoder: IRlpStreamDecoder<DArray>
{
    private readonly DArrayIndexDecoder _indexDecoder = new ();
    private readonly BitVectorDecoder _bitVecDecoder = new ();

    public int GetLength(DArray item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }

    private int GetContentLength(DArray item, RlpBehaviors rlpBehaviors)
    {
        int length = 0;
        length += _bitVecDecoder.GetLength(item._data, rlpBehaviors);
        length += _indexDecoder.GetLength(item._indexS0, rlpBehaviors);
        length += _indexDecoder.GetLength(item._indexS1, rlpBehaviors);
        return length;
    }

    public DArray Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        rlpStream.ReadSequenceLength();
        return new DArray(
            _bitVecDecoder.Decode(rlpStream),
            _indexDecoder.Decode(rlpStream),
            _indexDecoder.Decode(rlpStream)
        );
    }

    public void Encode(RlpStream stream, DArray item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);
        _bitVecDecoder.Encode(stream, item._data);
        _indexDecoder.Encode(stream, item._indexS0);
        _indexDecoder.Encode(stream, item._indexS1);
    }
}
