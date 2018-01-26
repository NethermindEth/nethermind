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

using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    public class AuthMessageV4 : IAuthMessage
    {
        public Signature Signature { get; set; }
        public PublicKey PublicKey { get; set; }
        public byte[] Nonce { get; set; }
        public int Version { get; private set; } = 4;

        public static AuthMessageV4 Decode(byte[] data)
        {
            Rlp rlp = new Rlp(data);
            object[] decodedRaw = (object[])Rlp.Decode(rlp);
            AuthMessageV4 authMessage = new AuthMessageV4();
            Signature signature = new Signature((byte[])decodedRaw[0]);
            authMessage.Signature = signature;
            authMessage.PublicKey = new PublicKey((byte[])decodedRaw[1]);
            authMessage.Nonce = (byte[])decodedRaw[2];
            authMessage.Version = ((byte[])decodedRaw[3]).ToInt32();
            return authMessage;
        }

        public static byte[] Encode(AuthMessageV4 authMessage)
        {
            return Rlp.Encode(
                Rlp.Encode(Bytes.Concat(authMessage.Signature.Bytes, authMessage.Signature.V)),
                Rlp.Encode(authMessage.PublicKey.PrefixedBytes.Slice(1, 64)),
                Rlp.Encode(authMessage.Nonce),
                Rlp.Encode(authMessage.Version)
            ).Bytes;
        }
    }
}