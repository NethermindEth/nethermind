// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

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

        public void Serialize(Span<byte> buffer, AckMessage msg)
        {
            msg.EphemeralPublicKey.Bytes.CopyTo(buffer.Slice(EphemeralPublicKeyOffset, EphemeralPublicKeyLength));
            msg.Nonce.CopyTo(buffer.Slice(NonceOffset, NonceLength));
            buffer[IsTokenUsedOffset] = msg.IsTokenUsed ? (byte)0x01 : (byte)0x00;
        }

        public int GetLength(AckMessage message, out int contentLength)
        {
            contentLength = TotalLength;
            return TotalLength;
        }

        public AckMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            if (data.Length != TotalLength)
            {
                throw new NetworkingException($"Incorrect incoming {nameof(AckMessage)} length. Expected {TotalLength} but was {data.Length}", NetworkExceptionType.Validation);
            }

            AckMessage authMessage = new();
            authMessage.EphemeralPublicKey = new PublicKey(data.Slice(EphemeralPublicKeyOffset, EphemeralPublicKeyLength));
            authMessage.Nonce = data.Slice(NonceOffset, NonceLength).ToArray();
            authMessage.IsTokenUsed = data[IsTokenUsedOffset] == 0x01;
            consumed = TotalLength;
            return authMessage;
        }
    }
}
