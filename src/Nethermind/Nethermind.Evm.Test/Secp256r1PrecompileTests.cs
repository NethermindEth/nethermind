// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public static IEnumerable<string> TestFiles()
        {
            yield return "p256Verify.json";
        }

        public static IPrecompile Precompile() => Secp256r1BoringPrecompile.Instance;

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
            (ReadOnlyMemory<byte> output, var success) = Precompile().Run(bytes, Prague.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(success, Is.True);
                Assert.That(output.ToArray(), Is.EquivalentTo(Array.Empty<byte>()));
            }
        }

        //[TestCase("4CEE90EB86EAA050036147A12D49004B6B9C72BD725D39D4785011FE190F0B4DC144FEF5B5D1FA47DE45845535A5AA676F0ACB12EFFD9914F02C756F74003503451845A945E6DF985596BF30B620AEE7A960DA014E9D2D4D96008FD4095467761A0D2106971B3ED7C85B78AC1D8548DEEA01887E1F0B9E4706377EC6B5C83681512CEFDDEAE2618F60E7E4CFEFCC2079CE3B7347CF19552AAEE8F3EEBF65CA27")] // fastcrypto only
        [TestCase("b9336a8d1f3e8ede001d19f41320bc7672d772a3d2cb0e435fff3c27d6804a2c1d75830cd36f4c9aa181b2c4221e87f176b7f05b7c87824e82e396c88315c407cb2acb01dac96efc53a32d4a0d85d0c2e48955214783ecf50a4f0414a319c05ae0fc6a6f50e1c57475673ee54e3a57f9a49f3328e743bf52f335e3eeaa3d28647f59d689c91e463607d9194d99faf316e25432870816dde63f5d4b373f12f22a")] // BoringSSL only?
        [TestCase("4cee90eb86eaa050036147a12d49004b6b9c72bd725d39d4785011fe190f0b4da73bd4903f0ce3b639bbbf6e8e80d16931ff4bcf5993d58468e8fb19086e8cac36dbcd03009df8c59286b162af3bd7fcc0450c9aa81be5d10d312af6c66b1d604aebd3099c618202fcfe16ae7770b0c49ab5eadf74b754204a3bb6060e44eff37618b065f9832de4ca6ca971a7a1adc826d0f7c00181a5fb2ddf79ae00b4e10e")]
        [TestCase("2c3f26f96a3ac0051df4989bffffffff9fd64886c1dc4f9924d8fd6f0edb048481f2359c4faba6b53d3e8c8c3fcc16a948350f7ab3a588b28c17603a431e39a8cd6f6a5cc3b55ead0ff695d06c6860b509e46d99fccefb9f7f9e101857f743002927b10512bae3eddcfe467828128bad2903269919f7086069c8c4df6c732838c7787964eaac00e5921fb1498a60f4606766b3d9685001558d1a974e7341513e")]
        public void CustomTest(string input)
        {
            var bytes = Bytes.FromHexString(input);
            (ReadOnlyMemory<byte> output, var success) = Precompile().Run(bytes, Prague.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(success, Is.True);
                Assert.That(output.ToArray(), Is.EquivalentTo(new byte[] { 1 }.PadLeft(32)));
            }
        }

        [Test]
        public void RandomTest()
        {
            var rng = RandomNumberGenerator.Create();
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            for (var i = 0; i < 1000; i++)
            {
                var hash = new byte[32];
                rng.GetBytes(hash);

                ECParameters pub = ecdsa.ExportParameters(false);
                byte[] sig = ecdsa.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                (byte[] x, byte[] y) = (pub.Q.X, pub.Q.Y);
                byte[] input = [.. hash, .. sig, .. x, .. y];

                (ReadOnlyMemory<byte> output, var success) = Precompile().Run(input, Prague.Instance);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(success, Is.True);
                    Assert.That(output.ToArray(), Is.EquivalentTo(new byte[] { 1 }.PadLeft(32)), $"#{i}: {input.ToHexString()}");
                }
            }
        }

        [TestCaseSource(nameof(TestSource))]
        public void TestSpeed(TestCase testCase)
        {
            Console.SetError(TestContext.Out);
            Console.SetOut(TestContext.Out);
            Console.WriteLine(testCase.Name);

            var watch = new Stopwatch();
            foreach (var precompile in new IPrecompile[]
                     {
                         Secp256r1Precompile.Instance,
                         Secp256r1RustPrecompile.Instance, Secp256r1FastCryptoPrecompile.Instance,
                         Secp256r1GoPrecompile.Instance, Secp256r1GoBoringPrecompile.Instance,
                         Secp256r1BoringPrecompile.Instance, Secp256r1BoringOptimizedPrecompile.Instance
                     })
            {
                watch.Restart();
                precompile.Run(testCase.Input, Prague.Instance);
                Console.WriteLine($"{precompile}: {watch.Elapsed.TotalMicroseconds}ms");
            }
        }

        [Test]
        public void ProfileTest()
        {
            var rng = RandomNumberGenerator.Create();
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            for (var i = 0; i < 1000; i++)
            {
                var hash = new byte[32];
                rng.GetBytes(hash);

                ECParameters pub = ecdsa.ExportParameters(false);
                byte[] sig = ecdsa.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                (byte[] x, byte[] y) = (pub.Q.X, pub.Q.Y);
                byte[] input = [.. hash, .. sig, .. x, .. y];

                Secp256r1BoringPrecompile.Instance.Run(input, Prague.Instance);
                Secp256r1BoringOptimizedPrecompile.Instance.Run(input, Prague.Instance);
            }
        }
    }
}
