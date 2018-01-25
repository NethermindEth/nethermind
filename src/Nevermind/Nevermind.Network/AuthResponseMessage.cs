/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    public class AuthResponseMessage
    {
        public const int EphemeralPublicKeyLength = 64;
        public const int EphemeralPublicKeyOffset = 0;
        public const int NonceLength = 32;
        public const int NonceOffset = EphemeralPublicKeyOffset + EphemeralPublicKeyLength;
        public const int IsTokenUsedLength = 1;
        public const int IsTokenUsedOffset = NonceOffset + NonceLength;
        private const int Length = IsTokenUsedOffset + IsTokenUsedLength;

        public PublicKey EphemeralPublicKey { get; set; }
        public byte[] Nonce { get; set; }
        public bool IsTokenUsed { get; set; }

        public static AuthResponseMessage Decode(byte[] data)
        {
            if (data.Length != Length)
            {
                throw new EthNetworkException($"Incorrect incoming {nameof(AuthResponseMessage)} length. Expected {Length} but was {data.Length}");
            }

            AuthResponseMessage authMessage = new AuthResponseMessage();
            authMessage.EphemeralPublicKey = new PublicKey(data.Slice(EphemeralPublicKeyOffset, EphemeralPublicKeyLength));
            authMessage.Nonce = data.Slice(NonceOffset, NonceLength);
            authMessage.IsTokenUsed = data[IsTokenUsedOffset] == 0x01;
            return authMessage;
        }

        public static byte[] Encode(AuthResponseMessage message)
        {
            byte[] data = new byte[Length];
            Buffer.BlockCopy(message.EphemeralPublicKey.PrefixedBytes, 1, data, EphemeralPublicKeyOffset, EphemeralPublicKeyLength);
            Buffer.BlockCopy(message.Nonce, 0, data, NonceOffset, NonceLength);
            data[IsTokenUsedOffset] = message.IsTokenUsed ? (byte)0x01 : (byte)0x00;
            return data;
        }
    }
}