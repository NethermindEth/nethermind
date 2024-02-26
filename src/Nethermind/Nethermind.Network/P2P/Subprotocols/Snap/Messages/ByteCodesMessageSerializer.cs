// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class ByteCodesMessageSerializer : IZeroMessageSerializer<ByteCodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, ByteCodesMessage message)
        {
            (int contentLength, int codesLength) = GetLength(message);
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.StartSequence(codesLength);
            for (int i = 0; i < message.Codes.Count; i++)
            {
                rlpStream.Encode(message.Codes[i]);
            }
        }

        public ByteCodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);

            rlpStream.ReadSequenceLength();

            long requestId = rlpStream.DecodeLong();
            IOwnedReadOnlyList<byte[]> result = rlpStream.DecodeArrayPoolList(stream => stream.DecodeByteArray());

            return new ByteCodesMessage(result) { RequestId = requestId };
        }

        public static (int contentLength, int codesLength) GetLength(ByteCodesMessage message)
        {
            int codesLength = 0;
            for (int i = 0; i < message.Codes.Count; i++)
            {
                codesLength += Rlp.LengthOf(message.Codes[i]);
            }

            return (Rlp.LengthOfSequence(codesLength) + Rlp.LengthOf(message.RequestId), codesLength);
        }
    }
}
