// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

#pragma warning disable CS0618 // ModExpPrecompilePreEip2565 is Obsolete
public class ModExpPrecompilePreEip2565Tests : PrecompileTests<ModExpPrecompilePreEip2565, ModExpPrecompilePreEip2565Tests>, IPrecompileTests
{
    static IEnumerable<string> IPrecompileTests.TestFiles()
    {
        yield return "modexp.json";
    }

    [TestCase(
        "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000002003fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2efffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f",
        "11",
        TestName = "pre2565: 32-byte modulus result, one trailing byte"
    )]
    [TestCase(
        "000000000000000000000000000000000000000000000000000000000000004000000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000040e09ad9675465c53a109fac66a445c91b292d2bb2c5268addb30cd82f80fcb0033ff97c80a5fc6f39193ae969c6ede6710a6b7ac27078a06d90ef1c72e5c85fb502fc9e1f6beb81516545975218075ec2af118cd8798df6e08a147c60fd6095ac2bb02c2908cf4dd7c81f11c289e4bce98f3553768f392a80ce22bf5c4f4a248c6b",
        "deadbeef",
        TestName = "pre2565: 64-byte modulus result, four trailing bytes"
    )]
    [TestCase(
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000",
        "cafebabe",
        TestName = "pre2565: baseLength=0 modulusLength=0, exp ignored"
    )]
    [TestCase(
        "000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000800000000000000000000000000000000000000000000000000000000000000000000001",
        "11223344",
        TestName = "pre2565: expLength>int.MaxValue overflow-safe SafeSlice"
    )]
    [TestCase(
        "0100000000000000000000000000000000000000000000000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000000001"
        + "0000000000000000000000000000000000000000000000000000000000000001",
        "aabbcc",
        TestName = "pre2565: baseLen byte[0] non-zero saturates ReadCappedLength"
    )]
    [TestCase(
        "0000000000000000000000000000000000000000000000000000000000000001"
        + "0100000000000000000000000000000000000000000000000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000000001",
        "aabbcc",
        TestName = "pre2565: expLen byte[0] non-zero saturates ReadCappedLength"
    )]
    [TestCase(
        "000000000000000000000000000000000000000000000000000000007fffffff"
        + "0000000000000000000000000000000000000000000000000000000000000001"
        + "0000000000000000000000000000000000000000000000000000000000000001",
        "aabbcc",
        TestName = "pre2565: baseLen=0x7FFFFFFF exact int.MaxValue boundary"
    )]
    [TestCase(
        "0000000000000000000000000000000000000000000000000000000000000001"
        + "000000000000000000000000000000000000000000000000000000007fffffff"
        + "0000000000000000000000000000000000000000000000000000000000000001",
        "aabbcc",
        TestName = "pre2565: expLen=0x7FFFFFFF exact int.MaxValue boundary"
    )]
    [TestCase(
        "0000000000000000000000000000000000000000000000000000000080000000"
        + "0000000000000000000000000000000000000000000000000000000000000001"
        + "0000000000000000000000000000000000000000000000000000000000000001",
        "aabbcc",
        TestName = "pre2565: baseLen=0x80000000 uint high bit set saturates ReadCappedLength"
    )]
    [TestCase(
        "0000000000000000000000000000000000000000000000000000000000000001"
        + "00000000000000000000000000000000000000000000000000000001ffffffff"
        + "0000000000000000000000000000000000000000000000000000000000000001",
        "aabbcc",
        TestName = "pre2565: expLen byte[27] non-zero saturates ReadCappedLength (last upper byte)"
    )]
    public void NormalizedInput_SameOutput(string input, string trailing) =>
        RunEffectiveInputTest(ModExpPrecompilePreEip2565.Instance, input, trailing, Byzantium.Instance);

    [TestCaseSource(typeof(ModExpPrecompileTests), nameof(ModExpPrecompileTests.OversizedLengths))]
    public void TestOversizedLengths(string input, string expectedOutput, bool status) =>
        RunTest(input, expectedOutput, status, Byzantium.Instance);
}
