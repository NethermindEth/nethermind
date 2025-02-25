// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Secp256r1PrecompileTests : VirtualMachineTestsBase
    {
        // ReSharper disable once ClassNeverInstantiated.Local
        public record TestCase(string Input, string Expected, string Name);

        private static IEnumerable<TestCase> TestSource()
        {
            // https://github.com/ethereum-optimism/op-geth/blob/7017b54770d480b5c8be63dc40eac9da166150f5/core/vm/testdata/precompiles/p256Verify.json
            var data = File.ReadAllText("TestFiles/p256Verify.json");
            return JsonSerializer.Deserialize<TestCase[]>(data);
        }

        [TestCaseSource(nameof(TestSource))]
        public void Produces_Correct_Outputs(TestCase testCase)
        {
            var input = Bytes.FromHexString(testCase.Input);
            var expected = Bytes.FromHexString(testCase.Expected);

            (ReadOnlyMemory<byte> output, bool success) = Secp256r1Precompile.Instance.Run(input, Prague.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(success, Is.True);
                Assert.That(output.ToArray(), Is.EquivalentTo(expected));
            }
        }

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
            (ReadOnlyMemory<byte> output, bool success) = Secp256r1Precompile.Instance.Run(bytes, Prague.Instance);
            success.Should().BeTrue();
            output.Should().Be(ReadOnlyMemory<byte>.Empty);
        }
    }
}
