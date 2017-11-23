using System;
using System.Threading;

namespace Nevermind.Core.Crypto
{
    public class PrivateKey
    {
        private const int PrivateKeyLengthInBytes = 32;
        private PublicKey _publicKey;

        public PrivateKey()
            :this(Random.GeneratePrivateKey())
        {
        }

        public PrivateKey(Hex key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (key.ByteLength != PrivateKeyLengthInBytes)
            {
                throw new ArgumentException($"{nameof(PrivateKey)} should be {PrivateKeyLengthInBytes} bytes long", nameof(key));
            }

            Hex = key;
        }

        public Hex Hex { get; set; }

        private PublicKey ComputePublicKey()
        {
            return new PublicKey(Secp256k1.Proxy.Proxy.GetPublicKey(Hex, false));
        }

        internal PublicKey PublicKey => LazyInitializer.EnsureInitialized(ref _publicKey, ComputePublicKey);

        public Address Address => PublicKey.Address;

        public override string ToString()
        {
            return Hex.ToString(true);
        }
    }
}