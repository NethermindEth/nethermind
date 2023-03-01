// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AuthEip8MessageSerializer : IZeroMessageSerializer<AuthEip8Message>
    {
        private readonly IMessagePad _messagePad;

        public AuthEip8MessageSerializer(IMessagePad messagePad)
        {
            _messagePad = messagePad;
        }

        public void Serialize(IByteBuffer byteBuffer, AuthEip8Message msg)
        {
            int totalLength = GetLength(msg);
            // TODO: Account for the padding
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(totalLength), true);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(totalLength);
            stream.Encode(Bytes.Concat(msg.Signature.Bytes, msg.Signature.RecoveryId));
            stream.Encode(msg.PublicKey.Bytes);
            stream.Encode(msg.Nonce);
            stream.Encode(msg.Version);
            _messagePad?.Pad(byteBuffer);
        }

        public int GetLength(AuthEip8Message msg)
        {
            int contentLength = Rlp.LengthOf(Bytes.Concat(msg.Signature.Bytes, msg.Signature.RecoveryId))
                                + Rlp.LengthOf(msg.PublicKey.Bytes)
                                + Rlp.LengthOf(msg.Nonce)
                                + Rlp.LengthOf(msg.Version);
            return contentLength;
        }

        public AuthEip8Message Deserialize(IByteBuffer msgBytes)
        {
            NettyRlpStream rlpStream = new(msgBytes);
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
