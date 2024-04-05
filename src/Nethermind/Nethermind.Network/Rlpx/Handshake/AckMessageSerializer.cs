// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AckMessageSerializer : IZeroMessageSerializer<AckMessage>
    {
        public const int EphemeralPublicKeyLength = 64;
        public const int EphemeralPublicKeyOffset = 0;
        public const int NonceLength = 32;
        public const int NonceOffset = EphemeralPublicKeyOffset + EphemeralPublicKeyLength;
        public const int IsTokenUsedLength = 1;
        public const int IsTokenUsedOffset = NonceOffset + NonceLength;
        public const int TotalLength = IsTokenUsedOffset + IsTokenUsedLength;

        public void Serialize(IByteBuffer byteBuffer, AckMessage msg)
        {
            byteBuffer.EnsureWritable(TotalLength);
            // TODO: find a way to now allocate this here
            byte[] data = new byte[TotalLength];
            Buffer.BlockCopy(msg.EphemeralPublicKey.Bytes, 0, data, EphemeralPublicKeyOffset, EphemeralPublicKeyLength);
            Buffer.BlockCopy(msg.Nonce, 0, data, NonceOffset, NonceLength);
            data[IsTokenUsedOffset] = msg.IsTokenUsed ? (byte)0x01 : (byte)0x00;
            byteBuffer.WriteBytes(data);
        }

        public AckMessage Deserialize(IByteBuffer msgBytes)
        {
            if (msgBytes.ReadableBytes != TotalLength)
            {
                throw new NetworkingException($"Incorrect incoming {nameof(AckMessage)} length. Expected {TotalLength} but was {msgBytes.ReadableBytes}", NetworkExceptionType.Validation);
            }

            AckMessage authMessage = new();
            authMessage.EphemeralPublicKey = new PublicKey(msgBytes.Slice(EphemeralPublicKeyOffset, EphemeralPublicKeyLength).ReadAllBytesAsSpan());
            authMessage.Nonce = msgBytes.Slice(NonceOffset, NonceLength).ReadAllBytesAsArray();
            authMessage.IsTokenUsed = msgBytes.GetByte(IsTokenUsedOffset) == 0x01;
            return authMessage;
        }
    }
}
