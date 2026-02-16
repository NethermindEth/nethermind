// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Network;

namespace Nethermind.Xdc.P2P.Eth100.Messages
{
    public class TimeoutP2PMessageSerializer : IZeroInnerMessageSerializer<TimeoutP2PMessage>
    {
        private readonly TimeoutDecoder _timeoutDecoder = new();

        public void Serialize(IByteBuffer byteBuffer, TimeoutP2PMessage message)
        {
            Rlp rlp = _timeoutDecoder.Encode(message.Timeout);
            byteBuffer.EnsureWritable(rlp.Length);
            byteBuffer.WriteBytes(rlp.Bytes);
        }

        public TimeoutP2PMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            var timeout = _timeoutDecoder.Decode(rlpStream);
            return new TimeoutP2PMessage(timeout);
        }

        public int GetLength(TimeoutP2PMessage message, out int contentLength)
        {
            Rlp rlp = _timeoutDecoder.Encode(message.Timeout);
            contentLength = rlp.Length;
            return contentLength;
        }
    }
}
