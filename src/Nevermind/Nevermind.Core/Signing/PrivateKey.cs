using System;
using System.Threading;
using Nevermind.Core.Encoding;

namespace Nevermind.Core.Signing
{
    public class PrivateKey
    {
        private const int PrivateKeyLengthInBytes = 32;
        private PublicKey _publicKey;

        public PrivateKey(string hexString)
            :this(HexString.ToBytes(hexString))
        {
        }

        public PrivateKey()
            :this(Random.GeneratePrivateKey())
        {
        }

        public PrivateKey(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != PrivateKeyLengthInBytes)
            {
                throw new ArgumentException($"{nameof(PrivateKey)} should be {PrivateKeyLengthInBytes} bytes long", nameof(bytes));
            }

            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        private PublicKey ComputePublicKey()
        {
            return new PublicKey(Secp256k1.Proxy.Proxy.GetPublicKey(Bytes, false));
        }

        public PublicKey PublicKey => LazyInitializer.EnsureInitialized(ref _publicKey, ComputePublicKey);

        public Address Address => PublicKey.Address;

        public override string ToString()
        {
            return HexString.FromBytes(Bytes);
        }
    }
}