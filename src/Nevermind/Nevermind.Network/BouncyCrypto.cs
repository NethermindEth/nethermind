/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nevermind.Core.Crypto;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;

namespace Nevermind.Network
{
    internal static class BouncyCrypto
    {
        internal static readonly ECDomainParameters DomainParameters;
        private static readonly SecureRandom SecureRandom = new SecureRandom();

        static BouncyCrypto()
        {
            X9ECParameters curveParamaters = SecNamedCurves.GetByName("secp256k1");
            DomainParameters = new ECDomainParameters(curveParamaters.Curve, curveParamaters.G, curveParamaters.N, curveParamaters.H);

            ECKeyPairGenerator generator = new ECKeyPairGenerator();
            ECKeyGenerationParameters keygeneratorParameters = new ECKeyGenerationParameters(DomainParameters, SecureRandom);
            generator.Init(keygeneratorParameters);
        }

        internal static ECPrivateKeyParameters WrapPrivateKey(PrivateKey privateKey)
        {
            BigInteger d = new BigInteger(1, privateKey.Hex);
            return new ECPrivateKeyParameters(d, DomainParameters);
        }

        internal static ECPublicKeyParameters WrapPublicKey(PublicKey publicKey)
        {
            ECPoint point = DomainParameters.Curve.DecodePoint(publicKey.PrefixedBytes);
            return new ECPublicKeyParameters(point, DomainParameters);
        }

        public static byte[] Agree(PrivateKey privateKey, PublicKey publicKey)
        {
            ECPrivateKeyParameters privateKeyParameters = WrapPrivateKey(privateKey);
            ECPublicKeyParameters publicKeyParameters = WrapPublicKey(publicKey);

            ECDHBasicAgreement agreement = new ECDHBasicAgreement();
            agreement.Init(privateKeyParameters);

            return agreement.CalculateAgreement(publicKeyParameters).ToByteArray();
        }
    }
}