//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;

namespace Nethermind.Crypto
{
    public partial class ProtectedData
    {
        // source https://stackoverflow.com/a/22247129
        private partial class AspNetWrapper
        {
            public static X509Certificate2 GenerateCertificate(string subjectName)
            {
                const int keyStrength = 2048;

                // Generating Random Numbers
                SecureRandom random = new SecureRandom(new CryptoApiRandomGenerator());

                // The Certificate Generator
                X509V3CertificateGenerator certificateGenerator = new X509V3CertificateGenerator();

                // Serial Number
                BigInteger serialNumber = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(long.MaxValue), random);
                certificateGenerator.SetSerialNumber(serialNumber);

                // Signature Algorithm
                const string signatureAlgorithm = "SHA256WithRSA";
                
                // Issuer and Subject Name
                X509Name subjectDn = new X509Name(subjectName);
                certificateGenerator.SetIssuerDN(subjectDn);
                certificateGenerator.SetSubjectDN(subjectDn);

                // Valid For
                DateTime notBefore = DateTime.UtcNow.Date;
                certificateGenerator.SetNotBefore(notBefore);
                certificateGenerator.SetNotAfter(notBefore.AddYears(2));

                // Subject Public Key
                RsaKeyPairGenerator keyPairGenerator = new RsaKeyPairGenerator();
                keyPairGenerator.Init(new KeyGenerationParameters(random, keyStrength));
                AsymmetricCipherKeyPair subjectKeyPair = keyPairGenerator.GenerateKeyPair();

                certificateGenerator.SetPublicKey(subjectKeyPair.Public);
                
                // Self sign certificate
                Org.BouncyCastle.X509.X509Certificate certificate = certificateGenerator.Generate(new Asn1SignatureFactory(signatureAlgorithm, subjectKeyPair.Private, random));
                return new X509Certificate2(certificate.GetEncoded());
            }
        }
    }
}
