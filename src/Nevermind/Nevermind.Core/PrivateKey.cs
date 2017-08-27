using System;
using System.Threading;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace Nevermind.Core
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
            //return new PublicKey(Secp256k1.Proxy.Proxy.GetPublicKey(Bytes, false));

            BigInteger d = new BigInteger(Bytes);
            ECPoint q = EC.DomainParameters.G.Multiply(d);

            var publicParams = new ECPublicKeyParameters(q, EC.DomainParameters);
            byte[] publicKey = publicParams.Q.GetEncoded(false);
            byte[] publicKeyCompressed = publicParams.Q.GetEncoded(true);
            return new PublicKey(publicKey, publicKeyCompressed);
        }

        public PublicKey PublicKey => LazyInitializer.EnsureInitialized(ref _publicKey, ComputePublicKey);

        public Address Address => PublicKey.Address;

        public override string ToString()
        {
            return HexString.FromBytes(Bytes);
        }
    }
}