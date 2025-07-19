// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Secp256r1PrecompileTests : PrecompileTests<Secp256r1PrecompileTests>, IPrecompileTests
    {
        private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);

        public static IEnumerable<string> TestFiles()
        {
            yield return "p256Verify.json";
        }

        public static IPrecompile Precompile() => Secp256r1Precompile.Instance;

        [Test]
        [TestCase(
            ""
        )]
        [TestCase(
            "4cee90eb86eaa050036147a12d49004b6a"
        )]
        [TestCase(
            "4cee90eb86eaa050036147a12d49004b6a958b991cfd78f16537fe6d1f4afd10273384db08bdfc843562a22b0626766686f6aec8247599f40bfe01bec0e0ecf17b4319559022d4d9bf007fe929943004eb4866760dedf319"
        )]
        public void Produces_Empty_Output_On_Invalid_Input(string input)
        {
            var bytes = Bytes.FromHexString(input);
            (ReadOnlyMemory<byte> output, var success) = Precompile().Run(bytes, Prague.Instance, isCacheable: false);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(success, Is.True);
                Assert.That(output.ToArray(), Is.EquivalentTo(Array.Empty<byte>()));
            }
        }

        [TestCaseSource(nameof(RandomECDsaInputs))]
        public void Verifies_random_valid_signature(byte[] input)
        {
            (ReadOnlyMemory<byte> output, var success) = Secp256r1Precompile.Instance.Run(input, Prague.Instance, isCacheable: false);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(success, Is.True);
                Assert.That(output.ToArray(), Is.EquivalentTo(ValidResult));
            }
        }

        public static IEnumerable<TestCaseData> RandomECDsaInputs()
        {
            var rng = RandomNumberGenerator.Create();
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            for (var i = 0; i < 100; i++)
            {
                var hash = new byte[32];
                rng.GetBytes(hash);

                ECParameters pub = ecdsa.ExportParameters(false);
                byte[] sig = ecdsa.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                byte[] input = [.. hash, .. sig, .. pub.Q.X, .. pub.Q.Y];

                yield return new TestCaseData(input).SetName(Convert.ToHexString(input));
            }
        }
    }
}
