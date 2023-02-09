// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

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

        public byte[] Serialize(AuthMessage msg)
        {
            byte[] data = new byte[Length];
            Buffer.BlockCopy(msg.Signature.Bytes, 0, data, SigOffset, SigLength - 1);
            data[SigLength - 1] = msg.Signature.RecoveryId;
            Buffer.BlockCopy(msg.EphemeralPublicHash.Bytes, 0, data, EphemeralHashOffset, EphemeralHashLength);
            Buffer.BlockCopy(msg.PublicKey.Bytes, 0, data, PublicKeyOffset, PublicKeyLength);
            Buffer.BlockCopy(msg.Nonce, 0, data, NonceOffset, NonceLength);
            data[IsTokenUsedOffset] = msg.IsTokenUsed ? (byte)0x01 : (byte)0x00;
            return data;
        }

        public AuthMessage Deserialize(byte[] msgBytes)
        {
            if (msgBytes.Length != Length)
            {
                throw new NetworkingException($"Incorrect incoming {nameof(AuthMessage)} length. Expected {Length} but was {msgBytes.Length}", NetworkExceptionType.Validation);
            }

            AuthMessage authMessage = new();
            authMessage.Signature = new Signature(msgBytes.AsSpan().Slice(SigOffset, SigLength - 1), msgBytes[64]);
            authMessage.EphemeralPublicHash = new Keccak(msgBytes.Slice(EphemeralHashOffset, EphemeralHashLength));
            authMessage.PublicKey = new PublicKey(msgBytes.AsSpan().Slice(PublicKeyOffset, PublicKeyLength));
            authMessage.Nonce = msgBytes.Slice(NonceOffset, NonceLength);
            authMessage.IsTokenUsed = msgBytes[IsTokenUsedOffset] == 0x01;
            return authMessage;
        }
    }
}
