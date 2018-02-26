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

namespace Nevermind.Network.Rlpx.Handshake
{
    public class AuthEip8MessageSerializer : IMessageSerializer<AuthEip8Message>
    {
        public byte[] Serialize(AuthEip8Message message, IMessagePad messagePad = null)
        {
            byte[] data = Rlp.Encode(
                Rlp.Encode(Bytes.Concat(message.Signature.Bytes, message.Signature.RecoveryId)),
                Rlp.Encode(message.PublicKey.Bytes),
                Rlp.Encode(message.Nonce),
                Rlp.Encode(message.Version)
            ).Bytes;

            return messagePad?.Pad(data) ?? data;
        }

        public AuthEip8Message Deserialize(byte[] data)
        {
            // TODO: support rlp without checking length?
            // TODO: this would not be compatible with future versions... ? if the length of prefixes changes
            Rlp rlp = new Rlp(data);
            object[] decodedRaw = (object[])Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);
            AuthEip8Message authMessage = new AuthEip8Message();
            byte[] sigAllbytes = (byte[])decodedRaw[0];
            Signature signature = new Signature(sigAllbytes.Slice(0, 64), sigAllbytes[64]); // since Signature class is Ethereum style it expects V as the 64th byte, hence we use RecoveryID constructor
            authMessage.Signature = signature;
            authMessage.PublicKey = new PublicKey((byte[])decodedRaw[1]);
            authMessage.Nonce = (byte[])decodedRaw[2];
            Debug.Assert(((byte[])decodedRaw[3]).ToInt32() >= 4, $"Expected {nameof(AuthEip8Message.Version)} to be greater than 4");
            return authMessage;
        }
    }
}