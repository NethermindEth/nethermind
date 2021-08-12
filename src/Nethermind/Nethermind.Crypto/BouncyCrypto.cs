//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
