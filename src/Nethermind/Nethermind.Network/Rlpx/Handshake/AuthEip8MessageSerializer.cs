// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AuthEip8MessageSerializer : IMessageSerializer<AuthEip8Message>
    {
        private const int SignatureLength = 65;

        public int GetLength(AuthEip8Message msg) => Rlp.LengthOfSequence(GetContentLength(msg));

        public int Serialize(Span<byte> destination, AuthEip8Message msg)
        {
            Span<byte> signature = stackalloc byte[SignatureLength];
            msg.Signature.Bytes.CopyTo(signature);
            signature[^1] = msg.Signature.RecoveryId;

            RlpWriter writer = new(destination);
            writer.StartSequence(GetContentLength(msg));
            writer.Encode(signature);
            writer.Encode(msg.PublicKey.Bytes);
            writer.Encode(msg.Nonce);
            writer.Encode(msg.Version);
            return writer.Position;
        }

        private static int GetContentLength(AuthEip8Message msg) =>
            Rlp.LengthOfByteString(SignatureLength, 0)
            + Rlp.LengthOf(msg.PublicKey.Bytes)
            + Rlp.LengthOf(msg.Nonce)
            + Rlp.LengthOf(msg.Version);

        /// <remarks>
        /// Ignores the trailing EIP-8 padding and any list elements after the nonce, as EIP-8 requires.
        /// </remarks>
        public AuthEip8Message Deserialize(ReadOnlySpan<byte> msgBytes)
        {
            RlpReader ctx = new(msgBytes);
            AuthEip8Message authMessage = new();
            ctx.ReadSequenceLength();
            ReadOnlySpan<byte> sigAllBytes = ctx.DecodeByteArraySpan(RlpLimit.L65);
            Signature signature = new(sigAllBytes[..64], sigAllBytes[64]); // since Signature class is Ethereum style it expects V as the 65th byte, hence we use RecoveryID constructor
            authMessage.Signature = signature;
            authMessage.PublicKey = new PublicKey(ctx.DecodeByteArraySpan(RlpLimit.L64));
            authMessage.Nonce = ctx.DecodeByteArray();
            return authMessage;
        }
    }
}
