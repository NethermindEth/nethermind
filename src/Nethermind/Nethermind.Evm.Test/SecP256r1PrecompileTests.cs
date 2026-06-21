// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class SecP256r1PrecompileTests : PrecompileTests<SecP256r1Precompile, SecP256r1PrecompileTests>, IPrecompileTests
    {
        private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);

        static IEnumerable<string> IPrecompileTests.TestFiles()
        {
            yield return "p256Verify.json";
        }

        [TestCase(
            "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            "11",
            TestName = "Valid input + 1 trailing byte")]
        [TestCase(
            "",
            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            TestName = "159-byte invalid input")]
        [TestCase(
            "",
            "0011",
            TestName = "2-byte invalid input")]
        public void NormalizedInput_SameOutput(string input, string trailing) => RunEffectiveInputTest(input, trailing);

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
            byte[] bytes = Bytes.FromHexString(input);
            (ReadOnlyMemory<byte> output, bool success) = Instance.Run(bytes, Prague.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(success, Is.True);
                Assert.That(output.ToArray(), Is.EqualTo(Array.Empty<byte>()));
            }
        }

        // Locks the hand-written limbs of the P-256 group order used by the zkVM precompile's
        // [1, n-1] scalar range check against the canonical big-endian encoding from SEC 2, 2.4.2.
        [Test]
        public void SecP256r1Curve_order_matches_canonical_value()
        {
            UInt256 expected = new(
                Bytes.FromHexString("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551"),
                isBigEndian: true);
            Assert.That(SecP256r1Curve.N, Is.EqualTo(expected));
        }

        [TestCaseSource(nameof(RandomECDsaInputs))]
        public void Verifies_random_valid_signature(byte[] input)
        {
            (ReadOnlyMemory<byte> output, bool success) = SecP256r1Precompile.Instance.Run(input, Prague.Instance);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(success, Is.True);
                Assert.That(output.ToArray(), Is.EqualTo(ValidResult));
            }
        }

        // EIP-7951 requires r and s to lie in [1, n-1]. A signature that is valid apart from an out-of-range
        // scalar must produce empty output, exercising both the standard verifier's range check and the zkVM
        // precompile's AreScalarsInRange guard (which keeps the secp256r1 accelerator from aborting the guest).
        [TestCaseSource(nameof(OutOfRangeScalarInputs))]
        public void Rejects_out_of_range_scalar(byte[] input)
        {
            (ReadOnlyMemory<byte> output, bool success) = SecP256r1Precompile.Instance.Run(input, Prague.Instance);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(success, Is.True);
                Assert.That(output.ToArray(), Is.EqualTo(Array.Empty<byte>()));
            }
        }

        public static IEnumerable<TestCaseData> RandomECDsaInputs()
        {
            RandomNumberGenerator rng = RandomNumberGenerator.Create();
            ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            for (int i = 0; i < 100; i++)
            {
                byte[] hash = new byte[32];
                rng.GetBytes(hash);

                byte[] input = BuildInput(ecdsa, hash);
                yield return new TestCaseData(input).SetName(Convert.ToHexString(input));
            }
        }

        public static IEnumerable<TestCaseData> OutOfRangeScalarInputs()
        {
            // r/s occupy bytes [32, 64) and [64, 96) of the 160-byte EIP-7951 input.
            const int rOffset = 32;
            const int sOffset = 64;
            byte[] zero = new byte[32];
            byte[] order = Bytes.FromHexString("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
            byte[] aboveOrder = Bytes.FromHexString("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] hash = new byte[32];
            RandomNumberGenerator.Fill(hash);
            byte[] valid = BuildInput(ecdsa, hash);

            foreach ((string name, int offset, byte[] value) in new[]
            {
                ("r=0", rOffset, zero),
                ("s=0", sOffset, zero),
                ("r=n", rOffset, order),
                ("s=n", sOffset, order),
                ("r>n", rOffset, aboveOrder),
                ("s>n", sOffset, aboveOrder),
            })
            {
                byte[] input = (byte[])valid.Clone();
                value.CopyTo(input, offset);
                yield return new TestCaseData(input).SetName($"Out-of-range scalar: {name}");
            }
        }

        private static byte[] BuildInput(ECDsa ecdsa, byte[] hash)
        {
            ECParameters pub = ecdsa.ExportParameters(false);
            byte[] sig = ecdsa.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return [.. hash, .. sig, .. pub.Q.X, .. pub.Q.Y];
        }
    }
}
