using System;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    public class AuthMessage
    {
        public const int SigLength = 65;
        public const int EphemeralHashLength = 32;
        public const int PublicKeyLength = 64;
        public const int NonceLength = 32;
        public const int IsTokenUsedLength = 1;

        private const int AuthMessageLength =
            SigLength +
            EphemeralHashLength +
            PublicKeyLength +
            NonceLength +
            IsTokenUsedLength;

        public Signature Signature { get; set; }
        public byte[] EphemeralPublicHash { get; set; }
        public PublicKey PublicKey { get; set; }
        public byte[] Nonce { get; set; }
        public bool IsTokenUsed { get; set; }

        public static AuthMessage Decode(byte[] data)
        {
            if (data.Length != AuthMessageLength)
            {
                throw new EthNetworkException($"Incorrect incoming {nameof(AuthMessage)} length. Expected {AuthMessageLength} but was {data.Length}");
            }

            AuthMessage authMessage = new AuthMessage();
            authMessage.Signature = new Signature(data.Slice(0, SigLength));
            authMessage.EphemeralPublicHash = data.Slice(SigLength, EphemeralHashLength);
            authMessage.PublicKey = new PublicKey(data.Slice(SigLength + EphemeralHashLength, PublicKeyLength));
            authMessage.Nonce = data.Slice(SigLength + EphemeralHashLength + PublicKeyLength, NonceLength);
            authMessage.IsTokenUsed = data[SigLength + EphemeralHashLength + PublicKeyLength + NonceLength] == 0x01;
            return authMessage;
        }

        public static byte[] Encode(AuthMessage authMessage)
        {
            byte[] data = new byte[AuthMessageLength];
            Buffer.BlockCopy(authMessage.Signature.Bytes, 0, data, 0, SigLength - 1);
            data[SigLength - 1] = authMessage.Signature.V; 
            Buffer.BlockCopy(authMessage.EphemeralPublicHash, 0, data, SigLength, EphemeralHashLength);
            Buffer.BlockCopy(authMessage.PublicKey.PrefixedBytes, 1, data, SigLength + EphemeralHashLength, PublicKeyLength);
            Buffer.BlockCopy(authMessage.Nonce, 0, data, SigLength + EphemeralHashLength + PublicKeyLength, NonceLength);
            data[SigLength + EphemeralHashLength + PublicKeyLength + NonceLength] = authMessage.IsTokenUsed ? (byte)0x01 : (byte)0x00;
            return data;
        }
    }
}