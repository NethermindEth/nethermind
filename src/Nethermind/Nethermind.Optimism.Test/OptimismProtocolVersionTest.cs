using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Optimism.Rpc;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismProtocolVersionTest
{
    private static IEnumerable<(string, OptimismProtocolVersionV0)> V0ParseCases()
    {
        yield return ("0x0000000000000000000000000000000000000000000000000000000000000000", new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0));
        yield return ("0x0000000000000000000000000000000000000000000000000000000000000100", new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 1));
        yield return ("0x0000000000000000000000000000000000000000000000000000010000000000", new OptimismProtocolVersionV0(new byte[8], 0, 0, 1, 0));
        yield return ("0x0000000000000000000000000000000000000400000003000000020000000100", new OptimismProtocolVersionV0(new byte[8], 4, 3, 2, 1));
        yield return ("0x0000000000000000000000000000000000000000000064000000020000000000", new OptimismProtocolVersionV0(new byte[8], 0, 100, 2, 0));
        yield return ("0x000000000000004f502d6d6f6400000000002a00000000000000020000000100", new OptimismProtocolVersionV0([(byte)'O', (byte)'P', (byte)'-', (byte)'m', (byte)'o', (byte)'d', 0, 0], 42, 0, 2, 1));
        yield return ("0x00000000000000626574612e3132330000000100000000000000000000000000", new OptimismProtocolVersionV0([(byte)'b', (byte)'e', (byte)'t', (byte)'a', (byte)'.', (byte)'1', (byte)'2', (byte)'3'], 1, 0, 0, 0));
        yield return ("0x0000000000000061620100000000000000002a00000000000000020000000000", new OptimismProtocolVersionV0([(byte)'a', (byte)'b', 1, 0, 0, 0, 0, 0], 42, 0, 2, 0));
        yield return ("0x0000000000000001020304050607080000002a00000000000000020000000000", new OptimismProtocolVersionV0([1, 2, 3, 4, 5, 6, 7, 8], 42, 0, 2, 0));
    }

    [TestCaseSource(nameof(V0ParseCases))]
    public void Parse_OptimismProtocolVersionV0((string HexString, OptimismProtocolVersionV0 Expected) testCase)
    {
        var bytes = Bytes.FromHexString(testCase.HexString);
        var actual = IOptimismProtocolVersion.Read(bytes);

        actual.Should().Be(testCase.Expected);
    }

    private static IEnumerable<(OptimismProtocolVersionV0, OptimismProtocolVersionV0, int)> V0CompareCases()
    {
        // Pre-release
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 1), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 2), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, uint.MaxValue), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), 0);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 42), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 42), 0);

        // Patch
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 1, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 2, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 1, 0), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, uint.MaxValue, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, uint.MaxValue - 1, 0), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), 0);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 101, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 101, 0), 0);

        // Minor
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 1, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 2, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 1, 0, 0), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, uint.MaxValue, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, uint.MaxValue - 1, 0, 0), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), 0);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 200, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 200, 0, 0), 0);

        // Major
        yield return (new OptimismProtocolVersionV0(new byte[8], 1, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 2, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 1, 0, 0, 0), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], uint.MaxValue, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], uint.MaxValue - 1, 0, 0, 0), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 0, 0, 0, 0), 0);
        yield return (new OptimismProtocolVersionV0(new byte[8], 300, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 300, 0, 0, 0), 0);

        // Mixed
        yield return (new OptimismProtocolVersionV0(new byte[8], 1, 2, 3, 4), new OptimismProtocolVersionV0(new byte[8], 1, 2, 3, 4), 0);
        yield return (new OptimismProtocolVersionV0(new byte[8], 1, 2, 4, 3), new OptimismProtocolVersionV0(new byte[8], 1, 2, 3, 4), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 1, 3, 3, 4), new OptimismProtocolVersionV0(new byte[8], 1, 2, 3, 4), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 2, 2, 3, 4), new OptimismProtocolVersionV0(new byte[8], 1, 2, 3, 4), 1);
        yield return (new OptimismProtocolVersionV0(new byte[8], 2, 0, 0, 0), new OptimismProtocolVersionV0(new byte[8], 1, 9, 9, 0), 1);
    }

    [TestCaseSource(nameof(V0CompareCases))]
    public void Compare_OptimismProtocolVersionV0((OptimismProtocolVersionV0 Left, OptimismProtocolVersionV0 Right, int Expected) testCase)
    {
        testCase.Left.CompareTo(testCase.Right).Should().Be(testCase.Expected);
        testCase.Right.CompareTo(testCase.Left).Should().Be(testCase.Expected * -1);
    }
}
