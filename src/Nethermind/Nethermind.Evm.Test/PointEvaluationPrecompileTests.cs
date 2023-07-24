// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PointEvaluationPrecompileTests
{
    private static readonly byte[] _predefinedSuccessAnswer =
        Bytes.FromHexString("000000000000000000000000000000000000000000000000000000000000100073eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001");

    private static readonly byte[] _predefinedFailureAnswer = Array.Empty<byte>();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => KzgPolynomialCommitments.InitializeAsync();

    [TestCaseSource(nameof(OutputTests))]
    public bool Test_PointEvaluationPrecompile_Produces_Correct_Outputs(byte[] input)
    {
        (ReadOnlyMemory<byte> output, bool success) = PointEvaluationPrecompile.Instance.Run(input, Cancun.Instance, null);
        output.ToArray().Should().BeEquivalentTo(success ? _predefinedSuccessAnswer : _predefinedFailureAnswer);
        return success;
    }

    public static IEnumerable<TestCaseData> OutputTests
    {
        get
        {
            TestCaseData AddExpectedResult(TestCaseData t, bool expectedResult) =>
                new(t.Arguments) { TestName = t.TestName + " - output", ExpectedResult = expectedResult };

            foreach (TestCaseData test in ValidTestCases)
            {
                yield return AddExpectedResult(test, true);
            }

            foreach (TestCaseData test in InvalidTestCases)
            {
                yield return AddExpectedResult(test, false);
            }
        }
    }

    [TestCaseSource(nameof(GasTests))]
    public void Test_PointEvaluationPrecompile_Has_Specific_Constant_Gas_Cost(byte[] input)
    {
        const long fixedGasCost = 50000;
        long gasSpent = PointEvaluationPrecompile.Instance.DataGasCost(input, Cancun.Instance) +
                        PointEvaluationPrecompile.Instance.BaseGasCost(Cancun.Instance);
        gasSpent.Should().Be(fixedGasCost);
    }

    public static IEnumerable<TestCaseData> GasTests => InvalidTestCases.Union(ValidTestCases);

    private static byte[] CreateKzgTestInput(string versionedHash, string z, string y, string commitment, string proof)
    {
        versionedHash.Length.Should().Be(64);
        z.Length.Should().Be(64);
        y.Length.Should().Be(64);
        commitment.Length.Should().Be(96);
        proof.Length.Should().Be(96);
        return Bytes.FromHexString(versionedHash + z + y + commitment + proof);
    }

    private static IEnumerable<TestCaseData> InvalidTestCases
    {
        get
        {
            yield return new TestCaseData(Bytes.FromHexString("00")) { TestName = "Incorrect data size - 2" };
            yield return new TestCaseData(Array.Empty<byte>()) { TestName = "Incorrect data size - 0" };
            yield return new TestCaseData(CreateKzgTestInput(
                "ff0657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Hash is not correctly versioned"
            };
            yield return new TestCaseData(CreateKzgTestInput(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014",
                "ff00000000000000000000000000000000000000000000000000000000000000",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Z is out of range"
            };
            yield return new TestCaseData(CreateKzgTestInput(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "ff00000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Y is out of range"
            };
            yield return new TestCaseData(CreateKzgTestInput(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "c10000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {

                TestName = "Commitment does not much hash"
            };
            yield return new TestCaseData(CreateKzgTestInput(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "c20000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Proof does not match"
            };

            yield return new TestCaseData(CreateKzgTestInput(
                "01d18459b334ffe8e2226eef1db874fda6db2bdd9357268b39220af2d59464fb",
                "5eb7004fe57383e6c88b99d839937fddf3f99279353aaf8d5c9a75f91ce33c62",
                "4882cf0609af8c7cd4c256e63a35838c95a9ebbf6122540ab344b42fd66d32e1",
                "978a0d595c823c05947b1156175e72634a377808384256e9921ebf72181890be2d6b58d4a73a880541d1656875654806",
                "95c51f028ec8ace94b2c24fff6662e4c61ad7b315b799aa5f40fcf5b36b2f1b6f9fc23bc66290aeef1de7e6ee4cb52ce"))
            {
                TestName = "Incorrect proof 1"
            };

            yield return new TestCaseData(CreateKzgTestInput(
                "01d18459b334ffe8e2226eef1db874fda6db2bdd9357268b39220af2d59464fb",
                "73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000000",
                "1522a4a7f34e1ea350ae07c29c96c7e79655aa926122e95fe69fcbd932ca49e9",
                "978a0d595c823c05947b1156175e72634a377808384256e9921ebf72181890be2d6b58d4a73a880541d1656875654806",
                "9418eb9a7cf2fa71125962f6662afeac10a7f1bbe26365995b13f6840946da49f79c7dfdd80b5b8a50bf44758cd2a96d"))
            {
                TestName = "Incorrect proof 2"
            };
        }
    }

    private static IEnumerable<TestCaseData> ValidTestCases
    {
        get
        {
            yield return new TestCaseData(CreateKzgTestInput(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Verification passes 1"
            };

            yield return new TestCaseData(CreateKzgTestInput(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014",
                "5eb7004fe57383e6c88b99d839937fddf3f99279353aaf8d5c9a75f91ce33c62",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Verification passes 2"
            };

            yield return new TestCaseData(CreateKzgTestInput(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014",
                "0000000000000000000000000000000000000000000000000000000000000001",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Verification passes 3"
            };

            yield return new TestCaseData(CreateKzgTestInput(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014",
                "564c0a11a0f704f4fc3e8acfe0f8245f0ad1347b378fbf96e206da11a5d36306",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Verification passes 4"
            };

            yield return new TestCaseData(CreateKzgTestInput(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014",
                "0000000000000000000000000000000000000000000000000000000000000002",
                "0000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Verification passes 5"
            };

            yield return new TestCaseData(CreateKzgTestInput(
                "01d18459b334ffe8e2226eef1db874fda6db2bdd9357268b39220af2d59464fb",
                "564c0a11a0f704f4fc3e8acfe0f8245f0ad1347b378fbf96e206da11a5d36306",
                "24d25032e67a7e6a4910df5834b8fe70e6bcfeeac0352434196bdf4b2485d5a1",
                "978a0d595c823c05947b1156175e72634a377808384256e9921ebf72181890be2d6b58d4a73a880541d1656875654806",
                "942307f266e636553e94006d11423f2688945ff3bdf515859eba1005c1a7708d620a94d91a1c0c285f9584e75ec2f82a"))
            {
                TestName = "Verification passes 6"
            };
        }
    }
}
