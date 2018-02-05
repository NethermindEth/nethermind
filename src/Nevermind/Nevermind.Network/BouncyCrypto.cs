using System;
using Nevermind.Core.Crypto;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;

namespace Nevermind.Network
{
    public static class BouncyCrypto
    {
        private static readonly ECDomainParameters DomainParameters;
        private static readonly SecureRandom SecureRandom = new SecureRandom();
        private static readonly ECKeyPairGenerator KeyPairGenerator;

        static BouncyCrypto()
        {
            X9ECParameters curveParamaters = SecNamedCurves.GetByName("secp256k1");
            DomainParameters = new ECDomainParameters(curveParamaters.Curve, curveParamaters.G, curveParamaters.N, curveParamaters.H);
//            CURVE_SPEC = new ECParameterSpec(params.getCurve(), params.getG(), params.getN(), params.getH());
//            HALF_CURVE_ORDER = params.getN().shiftRight(1);
//            secureRandom = new SecureRandom();

            ECKeyPairGenerator generator = new ECKeyPairGenerator();
            ECKeyGenerationParameters keygeneratorParameters = new ECKeyGenerationParameters(DomainParameters, SecureRandom);
            generator.Init(keygeneratorParameters);
            KeyPairGenerator = generator;
        }

        private static (ECPrivateKeyParameters, ECPublicKeyParameters) GenerateKeyPair()
        {
            AsymmetricCipherKeyPair keyPairParemeters = KeyPairGenerator.GenerateKeyPair();
            ECPrivateKeyParameters privateKeyParameters = (ECPrivateKeyParameters)keyPairParemeters.Private;
            ECPublicKeyParameters publicKeyParameters = (ECPublicKeyParameters)keyPairParemeters.Public;
            return (privateKeyParameters, publicKeyParameters);
        }

        private static (ECPrivateKeyParameters, ECPublicKeyParameters) WrapKeyPair(byte[] privateKey, byte[] publicKey)
        {
            return (WrapPrivateKey(privateKey), WrapPublicKey(publicKey));
        }

        private static ECPrivateKeyParameters WrapPrivateKey(byte[] bytes)
        {
            BigInteger d = new BigInteger(1, bytes);
            return new ECPrivateKeyParameters(d, DomainParameters);
        }

        private static ECPublicKeyParameters WrapPublicKey(byte[] bytes)
        {
            ECPoint point = DomainParameters.Curve.DecodePoint(bytes);
            return new ECPublicKeyParameters(point, DomainParameters);
        }

        public static byte[] Agree(PrivateKey privateKey, PublicKey publicKey)
        {
            ECDHBasicAgreement agreement = new ECDHBasicAgreement();
            agreement.Init(WrapPrivateKey(privateKey.Hex));

            BigInteger sharedSecret1 = agreement.CalculateAgreement(WrapPublicKey(publicKey.PrefixedBytes));
//            return sharedSecret1.ToByteArray();
            byte[] bytes = sharedSecret1.ToByteArray();
            byte[] theirMethod = BigIntegerToBytes(sharedSecret1);
            return theirMethod;
        }
        
        /// <summary>
        /// from EthereumJ
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private static byte[] BigIntegerToBytes(BigInteger value) {
            if (value == null)
                return null;

            byte[] data = value.ToByteArray();

            if (data.Length != 1 && data[0] == 0) {
                byte[] tmp = new byte[data.Length - 1];
                Array.Copy(data, 1, tmp, 0, tmp.Length);
                data = tmp;
            }
            return data;
        }
    }
}