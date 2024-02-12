using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Int256;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Tree.Utils;

public static class VerkleUtils
{
    private static FrE ValueExistsMarker { get; } = FrE.SetElement(BigInteger.Pow(2, 128));

    public static (FrE, FrE) BreakValueInLowHigh(byte[]? value)
    {
        if (value is null) return (FrE.Zero, FrE.Zero);
        UInt256 valueFr = new(value);
        FrE lowFr = FrE.SetElement(valueFr.u0, valueFr.u1) + ValueExistsMarker;
        var highFr = FrE.SetElement(valueFr.u2, valueFr.u3);
        return (lowFr, highFr);
    }

    public static int GetPathDifference(in ReadOnlySpan<byte> existingNodeKey,
        in ReadOnlySpan<byte> newNodeKey)
    {
        for (int i = 0; i < existingNodeKey.Length; i++) if (existingNodeKey[i] != newNodeKey[i]) return i;
        return existingNodeKey.Length;
    }
}
