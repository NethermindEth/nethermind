// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AuthMessageSerializer : IMessageSerializer<AuthMessage>
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

        public int GetLength(AuthMessage msg) => Length;

        public int Serialize(Span<byte> destination, AuthMessage msg)
        {
            if (msg.Nonce.Length != NonceLength)
            {
                throw new ArgumentException($"{nameof(AuthMessage)} nonce must be {NonceLength} bytes, not {msg.Nonce.Length}.", nameof(msg));
            }

            msg.Signature.Bytes.CopyTo(destination[SigOffset..]);
            destination[SigOffset + SigLength - 1] = msg.Signature.RecoveryId;
            msg.EphemeralPublicHash.Bytes.CopyTo(destination[EphemeralHashOffset..]);
            msg.PublicKey.Bytes.CopyTo(destination[PublicKeyOffset..]);
            msg.Nonce.CopyTo(destination.Slice(NonceOffset, NonceLength));
            destination[IsTokenUsedOffset] = msg.IsTokenUsed ? (byte)0x01 : (byte)0x00;
            return Length;
        }

        public AuthMessage Deserialize(ReadOnlySpan<byte> msg)
        {
            if (msg.Length != Length)
            {
                throw new NetworkingException($"Incorrect incoming {nameof(AuthMessage)} length. Expected {Length} but was {msg.Length}", NetworkExceptionType.Validation);
            }

            AuthMessage authMessage = new();
            authMessage.Signature = new Signature(msg[..(SigLength - 1)], msg[64]);
            authMessage.EphemeralPublicHash = new Hash256(msg.Slice(EphemeralHashOffset, EphemeralHashLength));
            authMessage.PublicKey = new PublicKey(msg.Slice(PublicKeyOffset, PublicKeyLength));
            authMessage.Nonce = msg.Slice(NonceOffset, NonceLength).ToArray();
            authMessage.IsTokenUsed = msg[IsTokenUsedOffset] == 0x01;
            return authMessage;
        }
    }
}
