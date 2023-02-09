// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AckEip8MessageSerializer : IMessageSerializer<AckEip8Message>
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

        public byte[] Serialize(AckEip8Message msg)
        {
            byte[] data = Rlp.Encode(
                Rlp.Encode(msg.EphemeralPublicKey.Bytes),
                Rlp.Encode(msg.Nonce),
                Rlp.Encode(msg.Version)
            ).Bytes;

            return _messagePad?.Pad(data) ?? data;
        }

        public AckEip8Message Deserialize(byte[] msgBytes)
        {
            RlpStream rlpStream = msgBytes.AsRlpStream();
            AckEip8Message authEip8Message = new();
            rlpStream.ReadSequenceLength();
            authEip8Message.EphemeralPublicKey = new PublicKey(rlpStream.DecodeByteArraySpan());
            authEip8Message.Nonce = rlpStream.DecodeByteArray();
            return authEip8Message;
        }
    }
}
