// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

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

        public void Serialize(Span<byte> buffer, AuthMessage msg)
        {
            msg.Signature.Bytes.CopyTo(buffer.Slice(SigOffset, SigLength - 1));
            buffer[SigLength - 1] = msg.Signature.RecoveryId;
            msg.EphemeralPublicHash.Bytes.CopyTo(buffer.Slice(EphemeralHashOffset, EphemeralHashLength));
            msg.PublicKey.Bytes.CopyTo(buffer.Slice(PublicKeyOffset, PublicKeyLength));
            msg.Nonce.CopyTo(buffer.Slice(NonceOffset, NonceLength));
            buffer[IsTokenUsedOffset] = msg.IsTokenUsed ? (byte)0x01 : (byte)0x00;
        }

        public int GetLength(AuthMessage message, out int contentLength)
        {
            contentLength = Length;
            return Length;
        }

        public AuthMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            if (data.Length != Length)
            {
                throw new NetworkingException($"Incorrect incoming {nameof(AuthMessage)} length. Expected {Length} but was {data.Length}", NetworkExceptionType.Validation);
            }

            AuthMessage authMessage = new();
            authMessage.Signature = new Signature(data[..(SigLength - 1)], data[SigLength - 1]);
            authMessage.EphemeralPublicHash = new Hash256(data.Slice(EphemeralHashOffset, EphemeralHashLength));
            authMessage.PublicKey = new PublicKey(data.Slice(PublicKeyOffset, PublicKeyLength));
            authMessage.Nonce = data.Slice(NonceOffset, NonceLength).ToArray();
            authMessage.IsTokenUsed = data[IsTokenUsedOffset] == 0x01;
            consumed = Length;
            return authMessage;
        }
    }
}
