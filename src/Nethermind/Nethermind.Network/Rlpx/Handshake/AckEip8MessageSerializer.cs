// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AckEip8MessageSerializer(IMessagePad messagePad) : IZeroMessageSerializer<AckEip8Message>
    {
        private readonly IMessagePad _messagePad = messagePad;
        public const int EphemeralPublicKeyLength = 64;
        public const int EphemeralPublicKeyOffset = 0;
        public const int NonceLength = 32;
        public const int NonceOffset = EphemeralPublicKeyOffset + EphemeralPublicKeyLength;
        public const int VersionOffset = NonceOffset + NonceLength;
        public const int TotalLength = EphemeralPublicKeyLength + NonceLength;

        public void Serialize(Span<byte> buffer, AckEip8Message msg)
        {
            GetLength(msg, out int contentLength);
            RlpWriter writer = new(buffer);
            writer.StartSequence(contentLength);
            writer.Encode(msg.EphemeralPublicKey.Bytes);
            writer.Encode(msg.Nonce);
            writer.Encode(msg.Version);
        }

        public int GetLength(AckEip8Message msg, out int contentLength)
        {
            contentLength = Rlp.LengthOf(msg.EphemeralPublicKey.Bytes)
                + Rlp.LengthOf(msg.Nonce)
                + Rlp.LengthOf(msg.Version);
            return Rlp.LengthOfSequence(contentLength);
        }

        public AckEip8Message Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            AckEip8Message msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }

        private static AckEip8Message Deserialize(ref RlpReader ctx)
        {
            AckEip8Message authEip8Message = new();
            ctx.ReadSequenceLength();
            authEip8Message.EphemeralPublicKey = new PublicKey(ctx.DecodeByteArraySpan(RlpLimit.L64));
            authEip8Message.Nonce = ctx.DecodeByteArray();
            return authEip8Message;
        }
    }
}
