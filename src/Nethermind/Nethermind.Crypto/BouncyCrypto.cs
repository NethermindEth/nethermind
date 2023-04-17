// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;

[assembly: InternalsVisibleTo("Nethermind.Network.Test")]
[assembly: InternalsVisibleTo("Nethermind.Network.Benchmark")]

namespace Nethermind.Crypto
{
    internal static class BouncyCrypto
    {
        internal static readonly ECDomainParameters DomainParameters;
        private static readonly SecureRandom SecureRandom = new();

        static BouncyCrypto()
        {
            X9ECParameters curveParameters = SecNamedCurves.GetByName("secp256k1");
            DomainParameters = new ECDomainParameters(curveParameters.Curve, curveParameters.G, curveParameters.N, curveParameters.H);

            ECKeyPairGenerator generator = new();
            ECKeyGenerationParameters keyGeneratorParameters = new(DomainParameters, SecureRandom);
            generator.Init(keyGeneratorParameters);
        }

        public static ECPrivateKeyParameters WrapPrivateKey(PrivateKey privateKey)
        {
            BigInteger d = new(1, privateKey.KeyBytes);
            return new ECPrivateKeyParameters(d, DomainParameters);
        }

        public static ECPublicKeyParameters WrapPublicKey(PublicKey publicKey)
        {
            ECPoint point = DomainParameters.Curve.DecodePoint(publicKey.PrefixedBytes);
            return new ECPublicKeyParameters(point, DomainParameters);
        }

        public static byte[] Agree(PrivateKey privateKey, PublicKey publicKey)
        {
            ECPrivateKeyParameters privateKeyParameters = WrapPrivateKey(privateKey);
            ECPublicKeyParameters publicKeyParameters = WrapPublicKey(publicKey);

            ECDHBasicAgreement agreement = new();
            agreement.Init(privateKeyParameters);

            byte[] agreementBytes = agreement.CalculateAgreement(publicKeyParameters).ToByteArray();
            return agreementBytes.Length > 32 ? agreementBytes.Slice(agreementBytes.Length - 32, 32) : agreementBytes.PadLeft(32);
        }
    }
}
