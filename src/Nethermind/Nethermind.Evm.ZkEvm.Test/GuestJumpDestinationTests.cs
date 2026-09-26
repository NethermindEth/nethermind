// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Differential tests for <see cref="JumpDestinationAnalyzer"/>'s scalar scan and the look-back in front of it.</summary>
/// <remarks>
/// The scalar scan is what a guest build runs: everywhere else <c>CreateJumpDestinationBitmap</c> finds
/// <c>Vector512</c> or <c>Vector128</c> accelerated and takes one of those instead, so it is never reached
/// through the real entry point. The bitmap feeds <see cref="JumpDestinationAnalyzer.ValidateJump"/>, so a
/// wrong bit is wrong execution rather than a slowdown. The guest walks a moving pointer and accumulates
/// flags per 64-bit segment; it is compared against the obvious indexed reference over the shapes where the
/// two could diverge: PUSH data truncated by the end of the code, the segment boundary the flags are
/// flushed on, every opcode value, and random bytecode.
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
        yield return Shape("leading STOP before JUMPDEST", [(byte)Instruction.STOP, JUMPDEST]);

        // 0x5c-0x5f: in range for the bias, but single-byte, which is the arm the bias reshuffled.
        yield return Shape("TLOAD/TSTORE/MCOPY/PUSH0 run",
            Code(8, (1, (byte)Instruction.TLOAD), (2, (byte)Instruction.TSTORE), (3, (byte)Instruction.MCOPY), (4, (byte)Instruction.PUSH0), (5, JUMPDEST)));

        // Each look-back steps back one byte through a PUSH1 run, so the first JUMPDEST after it runs out of
        // look-backs and falls back to the contiguous scan; the second is proven by its own.
        byte[] push1Run = Filled(120, PUSH1);
        push1Run[100] = JUMPDEST;
        push1Run[101] = JUMPDEST;
        yield return Shape("JUMPDESTs after a PUSH1 run longer than the look-back budget", push1Run);

        // Here each look-back steps back a whole PUSH32, so the search runs out of distance before look-backs.
        byte[] push32Run = Filled(400, PUSH32);
        push32Run[396] = JUMPDEST;
        yield return Shape("JUMPDEST after a PUSH32 run longer than the look-back distance", push32Run);

        yield return Shape("every byte a JUMPDEST", Filled(200, JUMPDEST));
        yield return Shape("every byte a PUSH32", Filled(200, PUSH32));
        yield return Shape("no byte in range", Filled(200, (byte)Instruction.STOP));
    }

    [TestCaseSource(nameof(Shapes))]
    public void Scan_matches_the_reference(byte[] code) => AssertMatchesReference(code);

    [Test]
    public void Full_analysis_reuses_the_completed_bitmap()
    {
        CodeInfo codeInfo = new(new byte[] { PUSH1, JUMPDEST, JUMPDEST });
        long[] bitmap = codeInfo.JumpDestinationBitmap;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeInfo.JumpDestinationBitmap, Is.SameAs(bitmap));
            Assert.That(codeInfo.ValidateJump(-1), Is.False);
            Assert.That(codeInfo.ValidateJump(0), Is.False);
            Assert.That(codeInfo.ValidateJump(1), Is.False);
            Assert.That(codeInfo.ValidateJump(2), Is.True);
            Assert.That(codeInfo.ValidateJump(3), Is.False);
        }
    }

    [Test]
    public void Stack_without_code_info_has_no_jump_destinations()
    {
        byte stackMemory = 0;
        EvmStack stack = new(0, ref stackMemory, new byte[] { JUMPDEST }, null);
        Assert.That(stack.IsJumpDestination(0), Is.False);
    }

    /// <remarks>
    /// The scan classifies a byte by comparing it <em>signed</em> against <c>[JUMPDEST, PUSH32]</c>, which
    /// is only equivalent across the whole byte range - which this walks. The trailing JUMPDESTs are enough that
    /// even a PUSH32 immediate stays inside the code, so the bytes an opcode masks are visible rather
    /// than truncated. The look-back classifies each of the 32 bytes before a destination against its own
    /// distance, so the byte also sits far enough in that the JUMPDESTs after it see it at every distance
    /// from a fresh cursor, where the look-back rather than the scan decides.
    /// </remarks>
    [Test]
    public void Scan_matches_the_reference_for_every_opcode_value([Range(0, 255)] int op, [Values(1, 40)] int position)
    {
        byte[] code = Filled(position + 40, JUMPDEST);
        code[position] = (byte)op;

        AssertMatchesReference(code);
    }

    [Test]
    public void Scan_matches_the_reference_over_random_bytecode([Range(1, 300)] int length, [Values] bool pushDense)
        => AssertMatchesReference(RandomCode(length, pushDense));

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
    /// JUMPDESTs - the only bytes that change the walk - too sparse to collide with each other. The
    /// sparser draw, one byte in eight from that window, leaves most JUMPDESTs out of every PUSH's reach,
    /// which is what lets the look-back prove them.
    /// </remarks>
    private static byte[] RandomCode(int length, bool pushDense)
    {
        byte[] code = new byte[length];
        uint state = Seed(length);
        uint uniformMask = pushDense ? 1u : 7u;
        for (int i = 0; i < length; i++)
        {
            state = Next(state);
            code[i] = (state & uniformMask) != 0 ? (byte)state : (byte)(0x58 + ((state >> 8) % 0x29));
        }

        return code;
    }

    private static uint Seed(int value) => ((uint)value * 2654435761u) | 1u;

    private static uint Next(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>Every position below <paramref name="length"/>, in a reproducible shuffled order.</summary>
    private static int[] Shuffled(int length)
    {
        int[] order = Forward(length);
        uint state = Seed(~length);
        for (int i = length - 1; i > 0; i--)
        {
            state = Next(state);
            int j = (int)(state % (uint)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    private static int[] Forward(int length)
    {
        int[] order = new int[length];
        for (int i = 0; i < length; i++)
        {
            order[i] = i;
        }

        return order;
    }

    [Test]
    public void Known_jump_destination_is_answered_only_for_scanned_code()
    {
        byte[] code = [JUMPDEST, PUSH1, JUMPDEST, JUMPDEST];
        CodeInfo codeInfo = new(code);
        byte stackMemory = 0;
        EvmStack stack = new(0, ref stackMemory, code, codeInfo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stack.IsKnownJumpDestination(3), Is.False, "not scanned yet");
            Assert.That(stack.IsJumpDestination(3), Is.True);
            Assert.That(stack.IsKnownJumpDestination(3), Is.True, "scanned by the jump above");
            Assert.That(stack.IsKnownJumpDestination(2), Is.False, "push data");
            Assert.That(stack.IsKnownJumpDestination(4), Is.False, "past the end");
        }
    }

    private static void AssertMatchesReference(byte[] code)
    {
        long[] expected = Reference(code);
        long[] actual = JumpDestinationAnalyzer.PopulateJumpDestinationBitmap_Scalar(
            JumpDestinationAnalyzer.CreateBitmap(code.Length), code);

        Assert.That(actual, Is.EqualTo(expected), () => Describe(code, expected, actual));
        if (code[0] == (byte)Instruction.STOP) expected = JumpDestinationAnalyzer.EmptyBitmap;

        int[] forward = Forward(code.Length);
        int[] backward = Forward(code.Length);
        Array.Reverse(backward);
        AssertQueriesMatch(code, expected, "forward", forward);
        AssertQueriesMatch(code, expected, "backward", backward);
        AssertQueriesMatch(code, expected, "shuffled", Shuffled(code.Length));
        AssertQueriesMatch(code, expected, "forward then backward", [.. forward, .. backward]);
    }

    /// <summary>Queries <paramref name="order"/> on one fresh code info, each query leaving its bitmap and cursor to the next.</summary>
    /// <remarks>
    /// Backward from a fresh cursor, destinations more than 32 bytes in are decided by the look-back and the
    /// local scans it leads to; forward, by the contiguous scan; shuffled, by both in turn.
    /// </remarks>
    private static void AssertQueriesMatch(byte[] code, long[] expected, string orderName, int[] order)
    {
        CodeInfo incremental = new(code);
        long[] incrementalBitmap = incremental.IncrementalJumpBitmap;
        byte stackMemory = 0;
        EvmStack stack = new(0, ref stackMemory, code, incremental);
        foreach (int i in order)
        {
            bool isJumpDestination = JumpDestinationAnalyzer.IsJumpDestination(expected, i);
            Assert.That(stack.IsJumpDestination(i), Is.EqualTo(isJumpDestination), $"stack {orderName} {i}");
            Assert.That(incremental.AnalyzeJump(i, incrementalBitmap, code), Is.EqualTo(isJumpDestination), $"{orderName} {i}");
        }
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
