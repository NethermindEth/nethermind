// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AckEip8MessageSerializer : IMessageSerializer<AckEip8Message>
    {
        public const int EphemeralPublicKeyLength = 64;
        public const int EphemeralPublicKeyOffset = 0;
        public const int NonceLength = 32;
        public const int NonceOffset = EphemeralPublicKeyOffset + EphemeralPublicKeyLength;
        public const int VersionOffset = NonceOffset + NonceLength;
        public const int TotalLength = EphemeralPublicKeyLength + NonceLength;

        public int GetLength(AckEip8Message msg) => Rlp.LengthOfSequence(GetContentLength(msg));

        public int Serialize(Span<byte> destination, AckEip8Message msg)
        {
            RlpWriter writer = new(destination);
            writer.StartSequence(GetContentLength(msg));
            writer.Encode(msg.EphemeralPublicKey.Bytes);
            writer.Encode(msg.Nonce);
            writer.Encode(msg.Version);
            return writer.Position;
        }

        private static int GetContentLength(AckEip8Message msg) =>
            Rlp.LengthOf(msg.EphemeralPublicKey.Bytes)
            + Rlp.LengthOf(msg.Nonce)
            + Rlp.LengthOf(msg.Version);

        public AckEip8Message Deserialize(ReadOnlySpan<byte> msgBytes)
        {
            RlpReader ctx = new(msgBytes);
            AckEip8Message authEip8Message = new();
            ctx.ReadSequenceLength();
            authEip8Message.EphemeralPublicKey = new PublicKey(ctx.DecodeByteArraySpan(RlpLimit.L64));
            authEip8Message.Nonce = ctx.DecodeByteArray();
            return authEip8Message;
        }
    }
}
