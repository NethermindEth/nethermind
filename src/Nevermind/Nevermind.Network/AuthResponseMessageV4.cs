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
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    public class AuthResponseMessageV4 : IAuthResponseMessage
    {
        public const int EphemeralPublicKeyLength = 64;
        public const int EphemeralPublicKeyOffset = 0;
        public const int NonceLength = 32;
        public const int NonceOffset = EphemeralPublicKeyOffset + EphemeralPublicKeyLength;
        public const int VersionOffset = NonceOffset + NonceLength;
        private const int Length = EphemeralPublicKeyLength + NonceLength;

        public PublicKey EphemeralPublicKey { get; set; }
        public byte[] Nonce { get; set; }
        public byte Version { get; set; } = 0x04;

        public static AuthResponseMessageV4 Decode(byte[] data)
        {
            Rlp rlp = new Rlp(data);
            object[] decodedRaw = (object[])Rlp.Decode(rlp);

            AuthResponseMessageV4 authMessage = new AuthResponseMessageV4();
            authMessage.EphemeralPublicKey = new PublicKey((byte[])decodedRaw[0]);
            authMessage.Nonce = (byte[])decodedRaw[1];
            // TODO: check the version? /Postel
            return authMessage;
        }

        public static byte[] Encode(AuthResponseMessageV4 message)
        {
            byte[] data = new byte[Length];
            Buffer.BlockCopy(message.EphemeralPublicKey.PrefixedBytes, 1, data, EphemeralPublicKeyOffset, EphemeralPublicKeyLength);
            Buffer.BlockCopy(message.Nonce, 0, data, NonceOffset, NonceLength);
            data[VersionOffset] = 0x04;
            return data;
        }

        public static byte[] Encode(AuthMessageV4 authMessage)
        {
            return Rlp.Encode(
                Rlp.Encode(authMessage.PublicKey.PrefixedBytes.Slice(1, 64)),
                Rlp.Encode(authMessage.Nonce),
                Rlp.Encode(authMessage.Version)
            ).Bytes;
        }
    }
}