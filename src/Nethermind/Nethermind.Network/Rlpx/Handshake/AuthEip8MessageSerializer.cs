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

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AuthEip8MessageSerializer : IZeroMessageSerializer<AuthEip8Message>
    {
        private readonly IMessagePad _messagePad;

        public AuthEip8MessageSerializer(IMessagePad messagePad)
        {
            _messagePad = messagePad;
        }

        public void Serialize(IByteBuffer byteBuffer, AuthEip8Message msg)
        {
            int totalLength = GetLength(msg);
            // TODO: Account for the padding
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(totalLength), true);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(totalLength);
            stream.Encode(Bytes.Concat(msg.Signature.Bytes, msg.Signature.RecoveryId));
            stream.Encode(msg.PublicKey.Bytes);
            stream.Encode(msg.Nonce);
            stream.Encode(msg.Version);
            _messagePad?.Pad(byteBuffer);
        }

        public int GetLength(AuthEip8Message msg)
        {
            int contentLength = Rlp.LengthOf(Bytes.Concat(msg.Signature.Bytes, msg.Signature.RecoveryId))
                                + Rlp.LengthOf(msg.PublicKey.Bytes)
                                + Rlp.LengthOf(msg.Nonce)
                                + Rlp.LengthOf(msg.Version);
            return contentLength;
        }

        public AuthEip8Message Deserialize(IByteBuffer msgBytes)
        {
            NettyRlpStream rlpStream = new(msgBytes);
            AuthEip8Message authMessage = new();
            rlpStream.ReadSequenceLength();
            ReadOnlySpan<byte> sigAllBytes = rlpStream.DecodeByteArraySpan();
            Signature signature = new(sigAllBytes.Slice(0, 64), sigAllBytes[64]); // since Signature class is Ethereum style it expects V as the 65th byte, hence we use RecoveryID constructor
            authMessage.Signature = signature;
            authMessage.PublicKey = new PublicKey(rlpStream.DecodeByteArraySpan());
            authMessage.Nonce = rlpStream.DecodeByteArray();
            int version = rlpStream.DecodeInt();
            return authMessage;
        }
    }
}
