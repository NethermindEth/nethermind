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
        Bytes.FromHexString("001000000000000001000000fffffffffe5bfeff02a4bd5305d8a10908d83933487d9d2953a7ed73");

    private static readonly byte[] _predefinedFailureAnswer = Array.Empty<byte>();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => KzgPolynomialCommitments.InitializeAsync();

    [TestCaseSource(nameof(OutputTests))]
    public bool Test_PointEvaluationPrecompile_Produces_Correct_Outputs(byte[] input)
    {
        (ReadOnlyMemory<byte> output, bool success) = PointEvaluationPrecompile.Instance.Run(input, Cancun.Instance);
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

    private static IEnumerable<TestCaseData> InvalidTestCases
    {
        get
        {
            yield return new TestCaseData(Bytes.FromHexString("00")) { TestName = "Incorrect data size - 2" };
            yield return new TestCaseData(Array.Empty<byte>()) { TestName = "Incorrect data size - 0" };
            yield return new TestCaseData(Bytes.FromHexString(
                "ff0657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Hash is not correctly versioned"
            };
            yield return new TestCaseData(Bytes.FromHexString(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401400000000000000000000000000000000000000000000000000000000000000ff0000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Z is out of range"
            };
            yield return new TestCaseData(Bytes.FromHexString(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000ffc00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Y is out of range"
            };
            yield return new TestCaseData(Bytes.FromHexString(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c10000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Commitment does not much hash"
            };
            yield return new TestCaseData(Bytes.FromHexString(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c20000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Proof does not match"
            };
        }
    }

    private static IEnumerable<TestCaseData> ValidTestCases
    {
        get
        {
            yield return new TestCaseData(Bytes.FromHexString(
                "010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"))
            {
                TestName = "Verification passes"
            };
        }
    }
}
