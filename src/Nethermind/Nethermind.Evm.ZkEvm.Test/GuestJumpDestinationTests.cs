// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Differential tests for <see cref="JumpDestinationAnalyzer"/>'s scalar scan.</summary>
/// <remarks>
/// The scan is word-at-a-time SWAR, and it is what a guest build runs: everywhere else
/// <c>CreateJumpDestinationBitmap</c> finds <c>Vector512</c> or <c>Vector128</c> accelerated and takes
/// one of those instead, so the scalar form is never reached through the real entry point. The bitmap
/// feeds <see cref="JumpDestinationAnalyzer.ValidateJump"/>, so a wrong bit is wrong execution rather
/// than a slowdown. It is compared against the obvious byte-at-a-time reference over the shapes where a
/// word-wise walk could diverge from it: PUSH data truncated by the end of the code, the 64-bit bitmap
/// segment boundary the flags are flushed on, every opcode value, and random bytecode.
/// </remarks>
public class GuestJumpDestinationTests
{
    private const byte JUMPDEST = (byte)Instruction.JUMPDEST;
    private const byte PUSH1 = (byte)Instruction.PUSH1;
    private const byte PUSH32 = (byte)Instruction.PUSH32;
    private const int BitsPerSegment = 64;

    private static IEnumerable<TestCaseData> Shapes()
    {
        // A PUSH whose immediates run off the end drives the walk past the last byte - PUSH32 as the
        // final byte overshoots by 32, the largest the scan can produce.
        yield return Shape("PUSH32 truncated at the tail", Code(8, (7, PUSH32)));
        yield return Shape("PUSH1 truncated at the tail", Code(8, (7, PUSH1)));
        yield return Shape("PUSH32 truncated with one immediate short", Code(40, (8, PUSH32)));

        // Immediates crossing 64 must not clear a JUMPDEST recorded in the previous segment.
        yield return Shape("PUSH32 data straddling the segment boundary", Code(160, (10, JUMPDEST), (50, PUSH32), (90, JUMPDEST)));
        yield return Shape("JUMPDESTs either side of the segment boundary",
            Code(200, (BitsPerSegment - 1, JUMPDEST), (BitsPerSegment, JUMPDEST), (BitsPerSegment + 1, JUMPDEST), ((2 * BitsPerSegment) - 1, JUMPDEST), (2 * BitsPerSegment, JUMPDEST)));

        yield return Shape("JUMPDEST inside PUSH data", Code(40, (0, PUSH32), (5, JUMPDEST), (33, JUMPDEST)));
        yield return Shape("JUMPDEST at position 0", Code(8, (0, JUMPDEST)));
        yield return Shape("JUMPDEST at the last byte", Code(8, (7, JUMPDEST)));

        // 0x5c-0x5f: in range for the bias, but single-byte, which is the arm the bias reshuffled.
        yield return Shape("TLOAD/TSTORE/MCOPY/PUSH0 run",
            Code(8, (1, (byte)Instruction.TLOAD), (2, (byte)Instruction.TSTORE), (3, (byte)Instruction.MCOPY), (4, (byte)Instruction.PUSH0), (5, JUMPDEST)));

        yield return Shape("every byte a JUMPDEST", Filled(200, JUMPDEST));
        yield return Shape("every byte a PUSH32", Filled(200, PUSH32));
        yield return Shape("no byte in range", Filled(200, (byte)Instruction.STOP));
    }

    [TestCaseSource(nameof(Shapes))]
    public void Scan_matches_the_reference(byte[] code) => AssertMatchesReference(code);

    /// <remarks>
    /// The scan classifies a byte by unsigned range checks against <c>[JUMPDEST, PUSH32]</c>, so it is
    /// only sound across the whole byte range - which this walks. The trailing JUMPDESTs are enough that
    /// even a PUSH32 immediate stays inside the code, so the bytes an opcode masks are visible rather
    /// than truncated.
    /// </remarks>
    [Test]
    public void Scan_matches_the_reference_for_every_opcode_value([Range(0, 255)] int op)
    {
        byte[] code = Filled(35, JUMPDEST);
        code[1] = (byte)op;

        AssertMatchesReference(code);
    }

    [Test]
    public void Scan_matches_the_reference_over_random_bytecode([Range(1, 300)] int length)
        => AssertMatchesReference(RandomCode(length));

    private static TestCaseData Shape(string name, byte[] code) => new TestCaseData(code).SetName($"{{m}}({name})");

    /// <summary>Bytecode of <paramref name="length"/> STOPs with each <paramref name="ops"/> entry stamped in.</summary>
    private static byte[] Code(int length, params (int Position, byte Op)[] ops)
    {
        byte[] code = new byte[length];
        foreach ((int position, byte op) in ops)
        {
            code[position] = op;
        }

        return code;
    }

    private static byte[] Filled(int length, byte op)
    {
        byte[] code = new byte[length];
        Array.Fill(code, op);
        return code;
    }

    /// <remarks>
    /// Xorshift rather than <see cref="Random"/> so the cases are reproducible across runtimes, and
    /// half the bytes are drawn from 0x58-0x80 because a uniform draw would leave PUSH runs and
    /// JUMPDESTs - the only bytes that change the walk - too sparse to collide with each other.
    /// </remarks>
    private static byte[] RandomCode(int length)
    {
        byte[] code = new byte[length];
        uint state = ((uint)length * 2654435761u) | 1u;
        for (int i = 0; i < length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            code[i] = (state & 1) != 0 ? (byte)state : (byte)(0x58 + ((state >> 8) % 0x29));
        }

        return code;
    }

    private static void AssertMatchesReference(byte[] code)
    {
        long[] expected = Reference(code);
        long[] actual = JumpDestinationAnalyzer.PopulateJumpDestinationBitmap_Scalar(
            JumpDestinationAnalyzer.CreateBitmap(code.Length), code);

        Assert.That(actual, Is.EqualTo(expected), () => Describe(code, expected, actual));
    }

    /// <summary>Walks byte by byte, marking every JUMPDEST and skipping PUSH immediates.</summary>
    private static long[] Reference(ReadOnlySpan<byte> code)
    {
        long[] bitmap = JumpDestinationAnalyzer.CreateBitmap(code.Length);
        for (int i = 0; i < code.Length; i++)
        {
            byte op = code[i];
            if (op == JUMPDEST)
            {
                bitmap[i / BitsPerSegment] |= 1L << i;
            }
            else if (op is >= PUSH1 and <= PUSH32)
            {
                i += op - PUSH1 + 1;
            }
        }

        return bitmap;
    }

    private static string Describe(byte[] code, long[] expected, long[] actual)
    {
        StringBuilder message = new($"code {Convert.ToHexString(code)}");
        for (int i = 0; i < code.Length; i++)
        {
            bool wanted = IsMarked(expected, i);
            if (wanted != IsMarked(actual, i))
            {
                message.Append($"; position {i} ({code[i]:x2}) should{(wanted ? "" : " not")} be marked");
            }
        }

        return message.ToString();
    }

    private static bool IsMarked(long[] bitmap, int position) => (bitmap[position / BitsPerSegment] & (1L << position)) != 0;
}
