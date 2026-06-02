// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Optimism.ProtocolVersion;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class OptimismProtocolVersionTest
{
    private static IEnumerable<TestCaseData> V0ReadWriteCases()
    {
        static TestCaseData Case(string name, string hexString, OptimismProtocolVersion.V0 expected) =>
            new TestCaseData(((string HexString, OptimismProtocolVersion.V0 Expected))(hexString, expected))
                .SetName(name);

        yield return Case("Zero", "0x0000000000000000000000000000000000000000000000000000000000000000", new(new byte[8], 0, 0, 0, 0));
        yield return Case("PrereleaseOne", "0x0000000000000000000000000000000000000000000000000000000000000100", new(new byte[8], 0, 0, 0, 1));
        yield return Case("PatchOne", "0x0000000000000000000000000000000000000000000000000000010000000000", new(new byte[8], 0, 0, 1, 0));
        yield return Case("MajorMinorPatchPrerelease", "0x0000000000000000000000000000000000000400000003000000020000000100", new(new byte[8], 4, 3, 2, 1));
        yield return Case("MinorPatch", "0x0000000000000000000000000000000000000000000064000000020000000000", new(new byte[8], 0, 100, 2, 0));
        yield return Case("OpModBuild", "0x000000000000004f502d6d6f6400000000002a00000000000000020000000100", new([(byte)'O', (byte)'P', (byte)'-', (byte)'m', (byte)'o', (byte)'d', 0, 0], 42, 0, 2, 1));
        yield return Case("BetaBuild", "0x00000000000000626574612e3132330000000100000000000000000000000000", new([(byte)'b', (byte)'e', (byte)'t', (byte)'a', (byte)'.', (byte)'1', (byte)'2', (byte)'3'], 1, 0, 0, 0));
        yield return Case("BinaryBuildWithZeros", "0x0000000000000061620100000000000000002a00000000000000020000000000", new([(byte)'a', (byte)'b', 1, 0, 0, 0, 0, 0], 42, 0, 2, 0));
        yield return Case("BinaryBuildNoZeros", "0x0000000000000001020304050607080000002a00000000000000020000000000", new([1, 2, 3, 4, 5, 6, 7, 8], 42, 0, 2, 0));
    }
    [TestCaseSource(nameof(V0ReadWriteCases))]
    public void OptimismProtocolVersionV0_ReadWrite((string HexString, OptimismProtocolVersion.V0 Expected) testCase)
    {
        byte[] bytes = Bytes.FromHexString(testCase.HexString);
        OptimismProtocolVersion actual = OptimismProtocolVersion.Read(bytes);

        byte[] bytesWritten = new byte[OptimismProtocolVersion.ByteLength];
        actual.Write(bytesWritten);
        string bytesWrittenHex = bytesWritten.ToHexString(withZeroX: true);

        Assert.That(actual, Is.EqualTo(testCase.Expected));
        Assert.That(testCase.HexString, Is.EqualTo(bytesWrittenHex));
    }

    private static IEnumerable<TestCaseData> V0CompareCases()
    {
        static TestCaseData Case(string name, OptimismProtocolVersion.V0 left, OptimismProtocolVersion.V0 right, int expected) =>
            new TestCaseData(((OptimismProtocolVersion.V0 Left, OptimismProtocolVersion.V0 Right, int Expected))(left, right, expected))
                .SetName(name);

        // Pre-release
        yield return Case("PrereleaseZeroLessThanOne", new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 1), 1);
        yield return Case("PrereleaseZeroLessThanTwo", new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 2), 1);
        yield return Case("PrereleaseZeroLessThanMax", new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, uint.MaxValue), 1);
        yield return Case("PrereleaseZeroEqualsZero", new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 0), 0);
        yield return Case("PrereleaseFortyTwoEqualsFortyTwo", new(new byte[8], 0, 0, 0, 42), new(new byte[8], 0, 0, 0, 42), 0);

        // Patch
        yield return Case("PatchOneGreaterThanZero", new(new byte[8], 0, 0, 1, 0), new(new byte[8], 0, 0, 0, 0), 1);
        yield return Case("PatchTwoGreaterThanOne", new(new byte[8], 0, 0, 2, 0), new(new byte[8], 0, 0, 1, 0), 1);
        yield return Case("PatchMaxGreaterThanPrevious", new(new byte[8], 0, 0, uint.MaxValue, 0), new(new byte[8], 0, 0, uint.MaxValue - 1, 0), 1);
        yield return Case("PatchZeroEqualsZero", new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 0), 0);
        yield return Case("PatchOneHundredOneEqualsOneHundredOne", new(new byte[8], 0, 0, 101, 0), new(new byte[8], 0, 0, 101, 0), 0);

        // Minor
        yield return Case("MinorOneGreaterThanZero", new(new byte[8], 0, 1, 0, 0), new(new byte[8], 0, 0, 0, 0), 1);
        yield return Case("MinorTwoGreaterThanOne", new(new byte[8], 0, 2, 0, 0), new(new byte[8], 0, 1, 0, 0), 1);
        yield return Case("MinorMaxGreaterThanPrevious", new(new byte[8], 0, uint.MaxValue, 0, 0), new(new byte[8], 0, uint.MaxValue - 1, 0, 0), 1);
        yield return Case("MinorZeroEqualsZero", new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 0), 0);
        yield return Case("MinorTwoHundredEqualsTwoHundred", new(new byte[8], 0, 200, 0, 0), new(new byte[8], 0, 200, 0, 0), 0);

        // Major
        yield return Case("MajorOneGreaterThanZero", new(new byte[8], 1, 0, 0, 0), new(new byte[8], 0, 0, 0, 0), 1);
        yield return Case("MajorTwoGreaterThanOne", new(new byte[8], 2, 0, 0, 0), new(new byte[8], 1, 0, 0, 0), 1);
        yield return Case("MajorMaxGreaterThanPrevious", new(new byte[8], uint.MaxValue, 0, 0, 0), new(new byte[8], uint.MaxValue - 1, 0, 0, 0), 1);
        yield return Case("MajorZeroEqualsZero", new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 0), 0);
        yield return Case("MajorThreeHundredEqualsThreeHundred", new(new byte[8], 300, 0, 0, 0), new(new byte[8], 300, 0, 0, 0), 0);

        // Mixed
        yield return Case("MixedEquals", new(new byte[8], 1, 2, 3, 4), new(new byte[8], 1, 2, 3, 4), 0);
        yield return Case("MixedPatchWins", new(new byte[8], 1, 2, 4, 3), new(new byte[8], 1, 2, 3, 4), 1);
        yield return Case("MixedMinorWins", new(new byte[8], 1, 3, 3, 4), new(new byte[8], 1, 2, 3, 4), 1);
        yield return Case("MixedMajorWins", new(new byte[8], 2, 2, 3, 4), new(new byte[8], 1, 2, 3, 4), 1);
        yield return Case("MixedMajorDominatesMinorPatch", new(new byte[8], 2, 0, 0, 0), new(new byte[8], 1, 9, 9, 0), 1);
    }
    [TestCaseSource(nameof(V0CompareCases))]
    public void OptimismProtocolVersionV0_Compare((OptimismProtocolVersion.V0 Left, OptimismProtocolVersion.V0 Right, int Expected) testCase)
    {
        Assert.That(testCase.Left.CompareTo(testCase.Right), Is.EqualTo(testCase.Expected));
        Assert.That(testCase.Right.CompareTo(testCase.Left), Is.EqualTo(testCase.Expected * -1));
    }

    [TestCase(4)]
    [TestCase(6)]
    [TestCase(9)]
    [TestCase(10)]
    public void OptimismProtocolVersionV0_BuildLengthIs8(int buildLength)
    {
        byte[] build = new byte[buildLength];

        Func<OptimismProtocolVersion> read = () => new OptimismProtocolVersion.V0(build, 0, 0, 0, 0);
        Assert.That(read, Throws.TypeOf<ArgumentException>());
    }

    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", false)]
    [TestCase("0x0010000000000000000000000000000000000000000000000000000000000000", true)]
    [TestCase("0x0001000000000000000000000000000000000000000000000000000000000000", true)]
    [TestCase("0x0000000000001000000000000000000000000000000000000000000000000000", true)]
    [TestCase("0x0000000000000100000000000000000000000000000000000000000000000000", true)]
    public void OptimismProtocolVersionV0_ReservedIsZero(string hexString, bool shouldThrow)
    {
        byte[] bytes = Bytes.FromHexString(hexString);
        Action read = () => OptimismProtocolVersion.Read(bytes);

        if (shouldThrow)
        {
            Assert.That(read, Throws.TypeOf<OptimismProtocolVersion.ParseException>());
        }
        else
        {
            Assert.That(read, Throws.Nothing);
        }
    }

    private static IEnumerable<TestCaseData> InvalidByteArrays()
    {
        yield return new TestCaseData(new byte[8]).SetName("InvalidLength_8");
        yield return new TestCaseData(new byte[16]).SetName("InvalidLength_16");
        yield return new TestCaseData(new byte[24]).SetName("InvalidLength_24");
    }
    [TestCaseSource(nameof(InvalidByteArrays))]
    public void OptimismProtocolVersion_Throws_Invalid_Length(byte[] bytes)
    {
        Action read = () => OptimismProtocolVersion.Read(bytes);
        Assert.That(read, Throws.TypeOf<OptimismProtocolVersion.ParseException>());
    }

    [TestCase(1)]
    [TestCase(5)]
    [TestCase(byte.MaxValue)]
    public void OptimismProtocolVersion_Throws_Unknown_Version(byte version)
    {
        byte[] bytes = new byte[32];
        bytes[0] = version;

        Action read = () => OptimismProtocolVersion.Read(bytes);
        Assert.That(read, Throws.TypeOf<OptimismProtocolVersion.ParseException>());
    }
}
