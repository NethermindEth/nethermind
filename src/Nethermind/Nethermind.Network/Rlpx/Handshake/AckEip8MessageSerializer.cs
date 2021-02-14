//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AckEip8MessageSerializer : IMessageSerializer<AckEip8Message>
    {
        private readonly IMessagePad _messagePad;
        public const int EphemeralPublicKeyLength = 64;
        public const int EphemeralPublicKeyOffset = 0;
        public const int NonceLength = 32;
        public const int NonceOffset = EphemeralPublicKeyOffset + EphemeralPublicKeyLength;
        public const int VersionOffset = NonceOffset + NonceLength;
        public const int TotalLength = EphemeralPublicKeyLength + NonceLength;

        public AckEip8MessageSerializer(IMessagePad messagePad)
        {
            _messagePad = messagePad;
        }
        
        public byte[] Serialize(AckEip8Message message)
        {
            byte[] data = Rlp.Encode(
                Rlp.Encode(message.EphemeralPublicKey.Bytes),
                Rlp.Encode(message.Nonce),
                Rlp.Encode(message.Version)
            ).Bytes;

            return _messagePad?.Pad(data) ?? data;
        }

        public AckEip8Message Deserialize(byte[] bytes)
        {
            RlpStream rlpStream = bytes.AsRlpStream();
            AckEip8Message authEip8Message = new();
            rlpStream.ReadSequenceLength();
            authEip8Message.EphemeralPublicKey = new PublicKey(rlpStream.DecodeByteArraySpan());
            authEip8Message.Nonce = rlpStream.DecodeByteArray();
            return authEip8Message;
        }
    }
}
