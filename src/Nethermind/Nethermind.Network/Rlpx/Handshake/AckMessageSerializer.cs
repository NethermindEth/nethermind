// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AckMessageSerializer : IMessageSerializer<AckMessage>
    {
        public const int EphemeralPublicKeyLength = 64;
        public const int EphemeralPublicKeyOffset = 0;
        public const int NonceLength = 32;
        public const int NonceOffset = EphemeralPublicKeyOffset + EphemeralPublicKeyLength;
        public const int IsTokenUsedLength = 1;
        public const int IsTokenUsedOffset = NonceOffset + NonceLength;
        public const int TotalLength = IsTokenUsedOffset + IsTokenUsedLength;

        public byte[] Serialize(AckMessage msg)
        {
            byte[] data = new byte[TotalLength];
            Buffer.BlockCopy(msg.EphemeralPublicKey.Bytes, 0, data, EphemeralPublicKeyOffset, EphemeralPublicKeyLength);
            Buffer.BlockCopy(msg.Nonce, 0, data, NonceOffset, NonceLength);
            data[IsTokenUsedOffset] = msg.IsTokenUsed ? (byte)0x01 : (byte)0x00;
            return data;
        }

        public AckMessage Deserialize(byte[] msgBytes)
        {
            if (msgBytes.Length != TotalLength)
            {
                throw new NetworkingException($"Incorrect incoming {nameof(AckMessage)} length. Expected {TotalLength} but was {msgBytes.Length}", NetworkExceptionType.Validation);
            }

            AckMessage authMessage = new();
            authMessage.EphemeralPublicKey = new PublicKey(msgBytes.AsSpan().Slice(EphemeralPublicKeyOffset, EphemeralPublicKeyLength));
            authMessage.Nonce = msgBytes.Slice(NonceOffset, NonceLength);
            authMessage.IsTokenUsed = msgBytes[IsTokenUsedOffset] == 0x01;
            return authMessage;
        }
    }
}
