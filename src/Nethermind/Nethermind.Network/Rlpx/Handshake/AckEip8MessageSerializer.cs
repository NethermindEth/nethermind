// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AckEip8MessageSerializer : IZeroMessageSerializer<AckEip8Message>
    {
        private readonly IMessagePad _messagePad;
        public const int EphemeralPublicKeyLength = 64;
        public const int EphemeralPublicKeyOffset = 0;
        public const int NonceLength = 32;
        public const int NonceOffset = EphemeralPublicKeyOffset + EphemeralPublicKeyLength;
        public const int VersionOffset = NonceOffset + NonceLength;
        public const int TotalLength = EphemeralPublicKeyLength + NonceLength;

        public AckEip8MessageSerializer(IMessagePad messagePad)
        {
            _messagePad = messagePad;
        }

        public void Serialize(IByteBuffer byteBuffer, AckEip8Message msg)
        {
            int totalLength = Rlp.LengthOf(msg.EphemeralPublicKey.Bytes);
            totalLength += Rlp.LengthOf(msg.Nonce);
            totalLength += Rlp.LengthOf(msg.Version);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(totalLength), true);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(totalLength);
            stream.Encode(msg.EphemeralPublicKey.Bytes);
            stream.Encode(msg.Nonce);
            stream.Encode(msg.Version);
        }

        public AckEip8Message Deserialize(IByteBuffer msgBytes)
        {
            NettyRlpStream rlpStream = new(msgBytes);
            AckEip8Message authEip8Message = new();
            rlpStream.ReadSequenceLength();
            authEip8Message.EphemeralPublicKey = new PublicKey(rlpStream.DecodeByteArraySpan());
            authEip8Message.Nonce = rlpStream.DecodeByteArray();
            return authEip8Message;
        }
    }
}
