// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class ModExpPrecompileTests : PrecompileTests<ModExpPrecompile, ModExpPrecompileTests>, IPrecompileTests
{
    [TestCase(
        "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000002003fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2efffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f",
        "11",
        TestName = "32-byte modulus result, one trailing byte"
    )]
    [TestCase(
        "000000000000000000000000000000000000000000000000000000000000004000000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000040e09ad9675465c53a109fac66a445c91b292d2bb2c5268addb30cd82f80fcb0033ff97c80a5fc6f39193ae969c6ede6710a6b7ac27078a06d90ef1c72e5c85fb502fc9e1f6beb81516545975218075ec2af118cd8798df6e08a147c60fd6095ac2bb02c2908cf4dd7c81f11c289e4bce98f3553768f392a80ce22bf5c4f4a248c6b",
        "deadbeef",
        TestName = "64-byte modulus result, four trailing bytes"
    )]
    [TestCase(
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000",
        "cafebabe",
        TestName = "baseLength=0 modulusLength=0, exp ignored"
    )]
    [TestCase(
        "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0000000000000000000000000000000000000000000000000000000000000001",
        "11223344",
        TestName = "expLength=uint.MaxValue overflow path"
    )]
    public void NormalizedInput_SameOutput(string input, string trailing)
    {
        RunEffectiveInputTest(Instance, input, trailing, Prague.Instance);
        RunEffectiveInputTest(Instance, input, trailing, Osaka.Instance);
    }


    [TestCaseSource(nameof(OversizedLengths))]
    public void TestOversizedLengths(string input, string expectedOutput, bool status) =>
        RunTest(input, expectedOutput, status, Prague.Instance);

    static IEnumerable<(string file, IReleaseSpec spec)> IPrecompileTests.TestFilesWithSpec()
    {
        yield return ("modexp_eip2565.json", Prague.Instance);
        yield return ("modexp_eip7883.json", Osaka.Instance);
    }

    public static IEnumerable<TestCaseData<string, string, bool>> OversizedLengths
    {
        get
        {
            yield return new(
                "000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000ffffffff000000000000000000000000000000000000000000000000000000000000000102",
                "00",
                true
            ) {TestName = "expLen=uint32.MaxValue (0xffffffff): huge expLength wraps modulus offset to base, must return zero (pre-EIP-7823)"};

            yield return new(
                "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007FFFFFFF02",
                "",
                false
            ) {TestName = "modLen=int32.MaxValue (0x7fffffff): rejected because of C# array size limit"};

            yield return new(
                "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008000000002",
                "",
                false
            ) {TestName = "modLen=int32.MaxValue+1 (0x80000000): rejected because of C# array size limit"};

            yield return new(
                "0000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000ffffffff02",
                "",
                false
            ) {TestName = "modLen=uint32.MaxValue (0xffffffff): rejected because of C# array size limit"};

            yield return new(
                "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000010000000002",
                "",
                false
            ) {TestName = "modLen=2^32 (0x100000000): rejected because of C# array size limit"};
        }
    }
}
