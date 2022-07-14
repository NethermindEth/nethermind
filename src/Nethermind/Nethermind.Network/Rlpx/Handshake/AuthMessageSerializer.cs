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
    public class AuthMessageSerializer : IZeroMessageSerializer<AuthMessage>
    {
        public const int SigOffset = 0;
        public const int SigLength = 65;
        public const int EphemeralHashOffset = SigOffset + SigLength;
        public const int EphemeralHashLength = 32;
        public const int PublicKeyOffset = EphemeralHashOffset + EphemeralHashLength;
        public const int PublicKeyLength = 64;
        public const int NonceOffset = PublicKeyOffset + PublicKeyLength;
        public const int NonceLength = 32;
        public const int IsTokenUsedLength = 1;
        public const int IsTokenUsedOffset = NonceOffset + NonceLength;
        public const int Length = IsTokenUsedOffset + IsTokenUsedLength;

        //  65 (sig)
        //  32 (ephem hash)
        //  64 (pub)
        //  32 (nonce)
        //   1 (token used)
        // =============
        // 194 (content)
        //  65 (pub)
        //  16 (IV)
        //  32 (MAC)
        // =============
        // 307 (total)

        public void Serialize(IByteBuffer byteBuffer, AuthMessage msg)
        {
            byteBuffer.EnsureWritable(Length, true);
            byte[] data = new byte[Length];
            Buffer.BlockCopy(msg.Signature.Bytes, 0, data, SigOffset, SigLength - 1);
            data[SigLength - 1] = msg.Signature.RecoveryId;
            Buffer.BlockCopy(msg.EphemeralPublicHash.Bytes, 0, data, EphemeralHashOffset, EphemeralHashLength);
            Buffer.BlockCopy(msg.PublicKey.Bytes, 0, data, PublicKeyOffset, PublicKeyLength);
            Buffer.BlockCopy(msg.Nonce, 0, data, NonceOffset, NonceLength);
            data[IsTokenUsedOffset] = msg.IsTokenUsed ? (byte)0x01 : (byte)0x00;
            byteBuffer.WriteBytes(data);
        }

        public AuthMessage Deserialize(IByteBuffer msgBytes)
        {
            if (msgBytes.ReadableBytes != Length)
            {
                throw new NetworkingException($"Incorrect incoming {nameof(AuthMessage)} length. Expected {Length} but was {msgBytes.ReadableBytes}", NetworkExceptionType.Validation);
            }

            AuthMessage authMessage = new();
            authMessage.Signature = new Signature(msgBytes.Slice(SigOffset, SigLength - 1).ReadAllBytes(), msgBytes.GetByte(64));
            authMessage.EphemeralPublicHash = new Keccak(msgBytes.Slice(EphemeralHashOffset, EphemeralHashLength).ReadAllBytes());
            authMessage.PublicKey = new PublicKey(msgBytes.Slice(PublicKeyOffset, PublicKeyLength).ReadAllBytes());
            authMessage.Nonce = msgBytes.Slice(NonceOffset, NonceLength).ReadAllBytes();
            authMessage.IsTokenUsed = msgBytes.GetByte(IsTokenUsedOffset) == 0x01;
            return authMessage;
        }
    }
}
