using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Optimism.Rpc;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class OptimismProtocolVersionTest
{
    private static IEnumerable<(string, OptimismProtocolVersion.V0)> V0ParseCases()
    {
        yield return ("0x0000000000000000000000000000000000000000000000000000000000000000", new(new byte[8], 0, 0, 0, 0));
        yield return ("0x0000000000000000000000000000000000000000000000000000000000000100", new(new byte[8], 0, 0, 0, 1));
        yield return ("0x0000000000000000000000000000000000000000000000000000010000000000", new(new byte[8], 0, 0, 1, 0));
        yield return ("0x0000000000000000000000000000000000000400000003000000020000000100", new(new byte[8], 4, 3, 2, 1));
        yield return ("0x0000000000000000000000000000000000000000000064000000020000000000", new(new byte[8], 0, 100, 2, 0));
        yield return ("0x000000000000004f502d6d6f6400000000002a00000000000000020000000100", new([(byte)'O', (byte)'P', (byte)'-', (byte)'m', (byte)'o', (byte)'d', 0, 0], 42, 0, 2, 1));
        yield return ("0x00000000000000626574612e3132330000000100000000000000000000000000", new([(byte)'b', (byte)'e', (byte)'t', (byte)'a', (byte)'.', (byte)'1', (byte)'2', (byte)'3'], 1, 0, 0, 0));
        yield return ("0x0000000000000061620100000000000000002a00000000000000020000000000", new([(byte)'a', (byte)'b', 1, 0, 0, 0, 0, 0], 42, 0, 2, 0));
        yield return ("0x0000000000000001020304050607080000002a00000000000000020000000000", new([1, 2, 3, 4, 5, 6, 7, 8], 42, 0, 2, 0));
    }
    [TestCaseSource(nameof(V0ParseCases))]
    public void Parse_OptimismProtocolVersionV0((string HexString, OptimismProtocolVersion.V0 Expected) testCase)
    {
        var bytes = Bytes.FromHexString(testCase.HexString);
        var actual = OptimismProtocolVersion.Read(bytes);

        actual.Should().Be(testCase.Expected);
    }

    private static IEnumerable<(OptimismProtocolVersion.V0, OptimismProtocolVersion.V0, int)> V0CompareCases()
    {
        // Pre-release
        yield return (new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 1), 1);
        yield return (new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 2), 1);
        yield return (new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, uint.MaxValue), 1);
        yield return (new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 0), 0);
        yield return (new(new byte[8], 0, 0, 0, 42), new(new byte[8], 0, 0, 0, 42), 0);

        // Patch
        yield return (new(new byte[8], 0, 0, 1, 0), new(new byte[8], 0, 0, 0, 0), 1);
        yield return (new(new byte[8], 0, 0, 2, 0), new(new byte[8], 0, 0, 1, 0), 1);
        yield return (new(new byte[8], 0, 0, uint.MaxValue, 0), new(new byte[8], 0, 0, uint.MaxValue - 1, 0), 1);
        yield return (new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 0), 0);
        yield return (new(new byte[8], 0, 0, 101, 0), new(new byte[8], 0, 0, 101, 0), 0);

        // Minor
        yield return (new(new byte[8], 0, 1, 0, 0), new(new byte[8], 0, 0, 0, 0), 1);
        yield return (new(new byte[8], 0, 2, 0, 0), new(new byte[8], 0, 1, 0, 0), 1);
        yield return (new(new byte[8], 0, uint.MaxValue, 0, 0), new(new byte[8], 0, uint.MaxValue - 1, 0, 0), 1);
        yield return (new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 0), 0);
        yield return (new(new byte[8], 0, 200, 0, 0), new(new byte[8], 0, 200, 0, 0), 0);

        // Major
        yield return (new(new byte[8], 1, 0, 0, 0), new(new byte[8], 0, 0, 0, 0), 1);
        yield return (new(new byte[8], 2, 0, 0, 0), new(new byte[8], 1, 0, 0, 0), 1);
        yield return (new(new byte[8], uint.MaxValue, 0, 0, 0), new(new byte[8], uint.MaxValue - 1, 0, 0, 0), 1);
        yield return (new(new byte[8], 0, 0, 0, 0), new(new byte[8], 0, 0, 0, 0), 0);
        yield return (new(new byte[8], 300, 0, 0, 0), new(new byte[8], 300, 0, 0, 0), 0);

        // Mixed
        yield return (new(new byte[8], 1, 2, 3, 4), new(new byte[8], 1, 2, 3, 4), 0);
        yield return (new(new byte[8], 1, 2, 4, 3), new(new byte[8], 1, 2, 3, 4), 1);
        yield return (new(new byte[8], 1, 3, 3, 4), new(new byte[8], 1, 2, 3, 4), 1);
        yield return (new(new byte[8], 2, 2, 3, 4), new(new byte[8], 1, 2, 3, 4), 1);
        yield return (new(new byte[8], 2, 0, 0, 0), new(new byte[8], 1, 9, 9, 0), 1);
    }
    [TestCaseSource(nameof(V0CompareCases))]
    public void Compare_OptimismProtocolVersionV0((OptimismProtocolVersion.V0 Left, OptimismProtocolVersion.V0 Right, int Expected) testCase)
    {
        testCase.Left.CompareTo(testCase.Right).Should().Be(testCase.Expected);
        testCase.Right.CompareTo(testCase.Left).Should().Be(testCase.Expected * -1);
    }

    private static IEnumerable<byte[]> InvalidByteArrays()
    {
        yield return new byte[8];
        yield return new byte[16];
        yield return new byte[24];
    }
    [TestCaseSource(nameof(InvalidByteArrays))]
    public void OptimismProtocolVersion_Throws_Invalid_Length(byte[] bytes)
    {
        Action read = () => OptimismProtocolVersion.Read(bytes);
        read.Should().Throw<OptimismProtocolVersion.ParseException>();
    }

    [TestCase(1)]
    [TestCase(5)]
    [TestCase(byte.MaxValue)]
    public void OptimismProtocolVersion_Throws_Unknown_Version(byte version)
    {
        var bytes = new byte[32];
        bytes[0] = version;

        Action read = () => OptimismProtocolVersion.Read(bytes);
        read.Should().Throw<OptimismProtocolVersion.ParseException>();
    }
}
