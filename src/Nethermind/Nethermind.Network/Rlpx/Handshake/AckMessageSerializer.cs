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

        public void Serialize(IByteBuffer byteBuffer, AckMessage msg)
        {
            byteBuffer.EnsureWritable(TotalLength);
            // TODO: find a way to now allocate this here
            byte[] data = new byte[TotalLength];
            Buffer.BlockCopy(msg.EphemeralPublicKey.Bytes, 0, data, EphemeralPublicKeyOffset, EphemeralPublicKeyLength);
            Buffer.BlockCopy(msg.Nonce, 0, data, NonceOffset, NonceLength);
            data[IsTokenUsedOffset] = msg.IsTokenUsed ? (byte)0x01 : (byte)0x00;
            byteBuffer.WriteBytes(data);
        }

        public AckMessage Deserialize(IByteBuffer msgBytes)
        {
            if (msgBytes.ReadableBytes != TotalLength)
            {
                throw new NetworkingException($"Incorrect incoming {nameof(AckMessage)} length. Expected {TotalLength} but was {msgBytes.ReadableBytes}", NetworkExceptionType.Validation);
            }

            AckMessage authMessage = new();
            authMessage.EphemeralPublicKey = new PublicKey(msgBytes.Slice(EphemeralPublicKeyOffset, EphemeralPublicKeyLength).ReadAllBytes());
            authMessage.Nonce = msgBytes.Slice(NonceOffset, NonceLength).ReadAllBytes();
            authMessage.IsTokenUsed = msgBytes.GetByte(IsTokenUsedOffset) == 0x01;
            return authMessage;
        }
    }
}
