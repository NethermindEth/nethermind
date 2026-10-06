// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AuthEip8MessageSerializer(IMessagePad messagePad) : IZeroMessageSerializer<AuthEip8Message>
    {
        private readonly IMessagePad _messagePad = messagePad;

        public void Serialize(Span<byte> buffer, AuthEip8Message msg)
        {
            GetLength(msg, out int contentLength);
            RlpWriter writer = new(buffer);
            writer.StartSequence(contentLength);
            writer.Encode(Bytes.Concat(msg.Signature.Bytes, msg.Signature.RecoveryId));
            writer.Encode(msg.PublicKey.Bytes);
            writer.Encode(msg.Nonce);
            writer.Encode(msg.Version);
            _messagePad.Pad(buffer.Slice(writer.Position));
        }

        /// <summary>
        /// Samples the random padding length once: the returned total sizes the rental for the
        /// <c>Serialize</c> call that follows, which pads exactly the buffer remainder, so the two
        /// always agree within one serialize operation. Do not call <c>GetLength</c> twice for one
        /// message and expect the same total.
        /// </summary>
        public int GetLength(AuthEip8Message msg, out int contentLength)
        {
            contentLength = Rlp.LengthOf(Bytes.Concat(msg.Signature.Bytes, msg.Signature.RecoveryId))
                                + Rlp.LengthOf(msg.PublicKey.Bytes)
                                + Rlp.LengthOf(msg.Nonce)
                                + Rlp.LengthOf(msg.Version);
            return Rlp.LengthOfSequence(contentLength) + _messagePad.GetPaddingLength();
        }

        public AuthEip8Message Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            AuthEip8Message msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }

        private static AuthEip8Message Deserialize(ref RlpReader ctx)
        {
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
