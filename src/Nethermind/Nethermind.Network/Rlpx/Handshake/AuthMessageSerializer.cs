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
using System.Buffers;
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
            byteBuffer.WriteBytes(msg.Signature.Bytes);
            byteBuffer.WriteByte(msg.Signature.RecoveryId);
            byteBuffer.WriteBytes(msg.EphemeralPublicHash.Bytes);
            byteBuffer.WriteBytes(msg.PublicKey.Bytes);
            byteBuffer.WriteBytes(msg.Nonce);
            byteBuffer.WriteByte(msg.IsTokenUsed ? 0x01 : 0x00);
        }

        public AuthMessage Deserialize(IByteBuffer msgBytes)
        {
            if (msgBytes.ReadableBytes != Length)
            {
                throw new NetworkingException($"Incorrect incoming {nameof(AuthMessage)} length. Expected {Length} but was {msgBytes.ReadableBytes}", NetworkExceptionType.Validation);
            }

            AuthMessage authMessage = new();
            Span<byte> msg = msgBytes.ReadAllBytesAsSpan();
            authMessage.Signature = new Signature(msg.Slice(SigOffset, SigLength - 1), msg[64]);
            authMessage.EphemeralPublicHash = new Keccak(msg.Slice(EphemeralHashOffset, EphemeralHashLength).ToArray());
            authMessage.PublicKey = new PublicKey(msg.Slice(PublicKeyOffset, PublicKeyLength));
            authMessage.Nonce = msg.Slice(NonceOffset, NonceLength).ToArray();
            authMessage.IsTokenUsed = msg[IsTokenUsedOffset] == 0x01;
            return authMessage;
        }
    }
}
