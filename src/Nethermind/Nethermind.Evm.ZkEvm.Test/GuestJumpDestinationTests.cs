// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Differential tests for the guest scan in <c>JumpDestinationAnalyzer.zkevm.cs</c>.</summary>
/// <remarks>
/// That body is stripped from every normal build (<c>Directory.Build.targets</c> drops
/// <c>*.zkevm.cs</c> unless <c>EnableZkEvm</c>), so nothing in <c>Nethermind.Evm.Test</c> can reach
/// it. The bitmap feeds <see cref="JumpDestinationAnalyzer.ValidateJump"/>, so a wrong bit is wrong
/// execution rather than a slowdown. The biased moving-reference walk is compared against the
/// obvious scalar reference over the shapes where the two forms could diverge: PUSH data truncated
/// by the end of the code, the 64-bit bitmap segment boundary the flags are flushed on, and the
/// opcodes the <c>JUMPDEST</c> bias reshuffled.
/// </remarks>
public class GuestJumpDestinationTests
{
    private const byte JUMPDEST = (byte)Instruction.JUMPDEST;
    private const byte PUSH1 = (byte)Instruction.PUSH1;
    private const byte PUSH32 = (byte)Instruction.PUSH32;
    private const int BitsPerSegment = 64;
    /// <summary>Furthest the scan can step past the end of the code: PUSH32 as its final byte.</summary>
    private const int MaxOvershoot = 32;

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
    /// The bias makes every byte outside <c>[JUMPDEST, PUSH32]</c> wrap to a large unsigned value
    /// rather than compare below zero, so the rejection arm is only sound across the whole byte
    /// range - which this walks. The trailing JUMPDESTs are enough that even a PUSH32 immediate
    /// stays inside the code, so the bytes an opcode masks are visible rather than truncated.
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

    /// <remarks>
    /// The scan is handed a slice of an oversized buffer, not <paramref name="code"/> itself: a
    /// truncated PUSH at the tail moves its byref up to <see cref="MaxOvershoot"/> bytes past the end,
    /// which is sound on the guest but not here, where a compacting GC can observe it. The slack keeps
    /// the overshoot inside the same object; the scan never reads past the slice, so the bitmap is
    /// unaffected.
    /// </remarks>
    private static void AssertMatchesReference(byte[] code)
    {
        long[] expected = Reference(code);
        byte[] padded = new byte[code.Length + MaxOvershoot];
        code.CopyTo(padded, 0);
        long[] actual = JumpDestinationAnalyzer.PopulateJumpDestinationBitmap_Scalar(
            JumpDestinationAnalyzer.CreateBitmap(code.Length), padded.AsSpan(0, code.Length));

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
