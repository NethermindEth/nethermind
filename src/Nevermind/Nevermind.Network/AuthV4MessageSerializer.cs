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

using System.Diagnostics;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    public class AuthV4MessageSerializer : IMessageSerializer<AuthV4Message>
    {
        public byte[] Serialize(AuthV4Message message, IMessagePad messagePad = null)
        {
            byte[] data = Rlp.Encode(
                Rlp.Encode(Bytes.Concat(message.Signature.Bytes, message.Signature.V)),
                Rlp.Encode(message.PublicKey.PrefixedBytes.Slice(1, 64)),
                Rlp.Encode(message.Nonce),
                Rlp.Encode(message.Version)
            ).Bytes;
            
            return messagePad?.Pad(data) ?? data;
        }

        public AuthV4Message Deserialize(byte[] data)
        {
            Rlp rlp = new Rlp(data);
            object[] decodedRaw = (object[])Rlp.Decode(rlp);
            AuthV4Message authMessage = new AuthV4Message();
            Signature signature = new Signature((byte[])decodedRaw[0]);
            authMessage.Signature = signature;
            authMessage.PublicKey = new PublicKey((byte[])decodedRaw[1]);
            authMessage.Nonce = (byte[])decodedRaw[2];
            Debug.Assert(((byte[])decodedRaw[3]).ToInt32() == 4, $"Expected {nameof(AuthV4Message.Version)} to be 4");
            return authMessage;
        }
    }
}