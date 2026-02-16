// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Network;

namespace Nethermind.Xdc.P2P.Eth100.Messages
{
    public class SyncInfoP2PMessageSerializer : IZeroInnerMessageSerializer<SyncInfoP2PMessage>
    {
        private readonly SyncInfoDecoder _syncInfoDecoder = new();

        public void Serialize(IByteBuffer byteBuffer, SyncInfoP2PMessage message)
        {
            Rlp rlp = _syncInfoDecoder.Encode(message.SyncInfo);
            byteBuffer.EnsureWritable(rlp.Length);
            byteBuffer.WriteBytes(rlp.Bytes);
        }

        public SyncInfoP2PMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            var syncInfo = _syncInfoDecoder.Decode(rlpStream);
            return new SyncInfoP2PMessage(syncInfo);
        }

        public int GetLength(SyncInfoP2PMessage message, out int contentLength)
        {
            Rlp rlp = _syncInfoDecoder.Encode(message.SyncInfo);
            contentLength = rlp.Length;
            return contentLength;
        }
    }
}
