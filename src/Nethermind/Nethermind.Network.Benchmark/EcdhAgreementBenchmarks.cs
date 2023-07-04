// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Utilities;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Network.Benchmarks
{
    public class EcdhAgreementBenchmarks
    {
        [GlobalSetup]
        public void Setup()
        {
            Check(Old(), Current());
        }

        private void Check(byte[] a, byte[] b)
        {
            if (!a.SequenceEqual(b))
            {
                Console.WriteLine($"Outputs are different {a.ToHexString()} != {b.ToHexString()}!");
                throw new InvalidOperationException();
            }

            Console.WriteLine($"Outputs are the same: {a.ToHexString()}");
        }

        [Benchmark]
        public byte[] Current()
        {
            byte[] result = SecP256k1.EcdhSerialized(ephemeral.Bytes, privateKey.KeyBytes);
            return result;
        }

        private static PrivateKey privateKey = new PrivateKey(Bytes.FromHexString("103aaccf80ad53c11ce2d1654e733a70835b852bfa4528a6214f11a9b9c6e55c"));// new PrivateKeyGenerator().Generate();
        private static PublicKey ephemeral = new PublicKey("7d2386471f6caf4327e08fe8767d5b3e3ae014a32ec2f1bd4f7ca3dcac7c00448f613f0ae0c2b340a06a2183586d4b36c0b33a19dba3cad5e9dd81278e1e5a9b"); // new PrivateKeyGenerator().Generate();

        [Benchmark]
        public byte[] Old()
        {
            ECPrivateKeyParameters privateKeyParameters = BouncyCrypto.WrapPrivateKey(privateKey);
            ECPublicKeyParameters publicKeyParameters = BouncyCrypto.WrapPublicKey(ephemeral);
            IBasicAgreement agreement = new ECDHBasicAgreement();
            agreement.Init(privateKeyParameters);

            BigInteger zAsInteger = agreement.CalculateAgreement(publicKeyParameters);
            byte[] bytes = BigIntegers.AsUnsignedByteArray(32, zAsInteger);
            return bytes;
        }
    }
}
