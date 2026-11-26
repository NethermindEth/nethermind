// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Stateless;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Zkvm.Abstractions;

class Program
{
    static int Main()
    {
        AirbenderRuntime.ReadPrehashedInput();
        if (!CheckWitnessFallback()) return 1;
        // The fixture repeats each hint so we can check both acceptance and the public fallback.
        if (!Check(23, 5, true) || !Check(23, 5, false) || !Check(23, 5, false)
            || !Check(0, 2, false) || !Check(0, 3, false) || !Check(23, 5, false)
            || !Check(23, 5, true) || !Check(23, 5, false))
            return 1;

        Span<byte> output = stackalloc byte[32];
        output.Clear();
        output[0] = 1;
        IO.WriteOutput(output);
        return 0;
    }

    static unsafe bool CheckWitnessFallback()
    {
        int reads = 0;
        byte[][] ReadNodes()
        {
            reads++;
            return [[1], [2], [3]];
        }

        using AirbenderWitnessNodes nodes = new(null, null, 3, ReadNodes);
        if (nodes.TryAttach() || nodes.Count != 3 || reads != 0) return false;
        if (nodes[1][0] != 2 || nodes.AsSpan()[2][0] != 3) return false;
        int index = 0;
        foreach (byte[] node in nodes)
            if (node[0] != ++index) return false;
        return index == 3 && reads == 1;
    }

    static bool Check(ulong numerator, ulong denominator, bool expectedAcceptance)
    {
        UInt256 dividend = numerator, divisor = denominator;
        bool accepted = WordDivision.TryHinted(in dividend, in divisor, out UInt256 q, out UInt256 r);
        if (accepted != expectedAcceptance || (accepted && (q != numerator / denominator || r != numerator % denominator)))
            return false;
        WordDivision.DivRem(in dividend, in divisor, out q, out r);
        return q == numerator / denominator && r == numerator % denominator;
    }
}
