// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AuthEip8MessageSerializer : IMessageSerializer<AuthEip8Message>
    {
        private readonly IMessagePad _messagePad;

        public AuthEip8MessageSerializer(IMessagePad messagePad)
        {
            _messagePad = messagePad;
        }

        public byte[] Serialize(AuthEip8Message msg)
        {
            byte[] data = Rlp.Encode(
                Rlp.Encode(Bytes.Concat(msg.Signature.Bytes, msg.Signature.RecoveryId)),
                Rlp.Encode(msg.PublicKey.Bytes),
                Rlp.Encode(msg.Nonce),
                Rlp.Encode(msg.Version)
            ).Bytes;

            return _messagePad?.Pad(data) ?? data;
        }

        public AuthEip8Message Deserialize(byte[] msgBytes)
        {
            RlpStream rlpStream = msgBytes.AsRlpStream();
            AuthEip8Message authMessage = new();
            rlpStream.ReadSequenceLength();
            ReadOnlySpan<byte> sigAllBytes = rlpStream.DecodeByteArraySpan();
            Signature signature = new(sigAllBytes.Slice(0, 64), sigAllBytes[64]); // since Signature class is Ethereum style it expects V as the 65th byte, hence we use RecoveryID constructor
            authMessage.Signature = signature;
            authMessage.PublicKey = new PublicKey(rlpStream.DecodeByteArraySpan());
            authMessage.Nonce = rlpStream.DecodeByteArray();
            int version = rlpStream.DecodeInt();
            return authMessage;
        }
    }
}
