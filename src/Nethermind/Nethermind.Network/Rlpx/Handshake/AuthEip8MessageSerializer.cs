// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AuthEip8MessageSerializer(IMessagePad messagePad) : IZeroMessageSerializer<AuthEip8Message>
    {
        private readonly IMessagePad _messagePad = messagePad;

        public void Serialize(IByteBuffer byteBuffer, AuthEip8Message msg)
        {
            int totalLength = GetLength(msg);
            // TODO: Account for the padding
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(totalLength));
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(totalLength);
            writer.Encode(Bytes.Concat(msg.Signature.Bytes, msg.Signature.RecoveryId));
            writer.Encode(msg.PublicKey.Bytes);
            writer.Encode(msg.Nonce);
            writer.Encode(msg.Version);
            _messagePad?.Pad(byteBuffer);
        }

        public static int GetLength(AuthEip8Message msg)
        {
            int contentLength = Rlp.LengthOf(Bytes.Concat(msg.Signature.Bytes, msg.Signature.RecoveryId))
                                + Rlp.LengthOf(msg.PublicKey.Bytes)
                                + Rlp.LengthOf(msg.Nonce)
                                + Rlp.LengthOf(msg.Version);
            return contentLength;
        }

        public AuthEip8Message Deserialize(IByteBuffer msgBytes) =>
            msgBytes.DeserializeRlp(Deserialize) ?? throw new RlpException("Auth EIP-8 message decoding returned null.");

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
