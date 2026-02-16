// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Network;

namespace Nethermind.Xdc.P2P.Eth100.Messages
{
    public class QuorumCertificateP2PMessageSerializer : IZeroInnerMessageSerializer<QuorumCertificateP2PMessage>
    {
        private readonly QuorumCertificateDecoder _qcDecoder = new();

        public void Serialize(IByteBuffer byteBuffer, QuorumCertificateP2PMessage message)
        {
            Rlp rlp = _qcDecoder.Encode(message.QuorumCertificate);
            byteBuffer.EnsureWritable(rlp.Length);
            byteBuffer.WriteBytes(rlp.Bytes);
        }

        public QuorumCertificateP2PMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            var qc = _qcDecoder.Decode(rlpStream);
            return new QuorumCertificateP2PMessage(qc);
        }

        public int GetLength(QuorumCertificateP2PMessage message, out int contentLength)
        {
            Rlp rlp = _qcDecoder.Encode(message.QuorumCertificate);
            contentLength = rlp.Length;
            return contentLength;
        }
    }
}
