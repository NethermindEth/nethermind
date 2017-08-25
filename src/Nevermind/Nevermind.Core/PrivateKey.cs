using System;
using System.Threading;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace Nevermind.Core
{
    public class PrivateKey
    {
        private static readonly X9ECParameters Curve = SecNamedCurves.GetByName("secp256k1");
        private static readonly ECDomainParameters Domain = new ECDomainParameters(Curve.Curve, Curve.G, Curve.N, Curve.H);

        private const int PrivateKeyLengthInBytes = 32;
        private readonly byte[] _privateKey;
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

            _privateKey = bytes;
        }
        
        private PublicKey ComputePublicKey()
        {
            BigInteger d = new BigInteger(_privateKey);
            ECPoint q = Domain.G.Multiply(d);

            var publicParams = new ECPublicKeyParameters(q, Domain);
            byte[] publicKey = publicParams.Q.GetEncoded();
            return new PublicKey(publicKey);
        }

        public PublicKey PublicKey => LazyInitializer.EnsureInitialized(ref _publicKey, ComputePublicKey);

        public override string ToString()
        {
            return HexString.FromBytes(_privateKey);
        }
    }
}