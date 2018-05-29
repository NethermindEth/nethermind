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
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AuthEip8MessageSerializer : IMessageSerializer<AuthEip8Message>
    {
        private readonly IMessagePad _messagePad;

        public AuthEip8MessageSerializer(IMessagePad messagePad)
        {
            _messagePad = messagePad;
        }
        
        public byte[] Serialize(AuthEip8Message message)
        {
            byte[] data = Rlp.Encode(
                Rlp.Encode(Bytes.Concat(message.Signature.Bytes, message.Signature.RecoveryId)),
                Rlp.Encode(message.PublicKey.Bytes),
                Rlp.Encode(message.Nonce),
                Rlp.Encode(message.Version)
            ).Bytes;

            return _messagePad?.Pad(data) ?? data;
        }

        public AuthEip8Message Deserialize(byte[] data)
        {
            // TODO: support rlp without checking length?
            // TODO: this would not be compatible with future versions... ? if the length of prefixes changes
            Rlp.DecoderContext context = data.AsRlpContext();
            AuthEip8Message authMessage = new AuthEip8Message();
            context.ReadSequenceLength();
            byte[] sigAllbytes = context.DecodeByteArray();
            Signature signature = new Signature(sigAllbytes.Slice(0, 64), sigAllbytes[64]); // since Signature class is Ethereum style it expects V as the 64th byte, hence we use RecoveryID constructor
            authMessage.Signature = signature;
            authMessage.PublicKey = new PublicKey(context.DecodeByteArray());
            authMessage.Nonce = context.DecodeByteArray();
            int version = context.DecodeInt();
            Debug.Assert(version >= 4, $"Expected {nameof(AuthEip8Message.Version)} to be greater than 4");
            return authMessage;
        }
    }
}