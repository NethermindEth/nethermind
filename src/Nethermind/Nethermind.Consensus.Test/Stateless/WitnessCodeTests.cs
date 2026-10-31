// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Stateless.Execution.IO;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class WitnessCodeTests
{
    private const int Padding = CodeInfo.ExecutionPadding;

    [Test]
    public void Moves_codes_to_the_front_of_the_input_padded()
    {
        byte[] input = GC.AllocateArray<byte>(256, pinned: true);
        SszWitnessCodes[] codes = InInput(input, (100, 5), (140, 3), (150, 10));
        byte[][] expected = Contents(codes);

        int taken = WitnessCode.MoveToFront(codes, input);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken, Is.EqualTo(128));
            AssertPlacedAt(codes[0], input, 0, expected[0]);
            AssertPlacedAt(codes[1], input, 40, expected[1]);
            AssertPlacedAt(codes[2], input, 80, expected[2]);
        }
    }

    [Test]
    public void Copies_a_code_that_would_overwrite_the_next_one()
    {
        byte[] input = GC.AllocateArray<byte>(256, pinned: true);
        // The first code padded would reach the second; the second then moves over its own source.
        SszWitnessCodes[] codes = InInput(input, (10, 4), (20, 4), (200, 8));
        byte[][] expected = Contents(codes);

        int taken = WitnessCode.MoveToFront(codes, input);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken, Is.EqualTo(88));
            Assert.That(ExecutableCodeMemory.IsPlaced(codes[0].Bytes.Code), Is.False);
            Assert.That(ExecutableCodeMemory.TryGetExecutionBuffer(codes[0].Bytes.Code, out _), Is.True);
            Assert.That(codes[0].Bytes.Code.ToArray(), Is.EqualTo(expected[0]));
            AssertPlacedAt(codes[1], input, 0, expected[1]);
            AssertPlacedAt(codes[2], input, 40, expected[2]);
        }
    }

    [TestCase(256, 200, 100, TestName = "Copies_every_code_out_of_input_order")]
    // The first code padded would reach past the input, which ends before the second code.
    [TestCase(64, 10, 100, TestName = "Copies_every_code_when_one_lies_past_the_input")]
    public void Copies_every_code(int consumedLength, int firstOffset, int secondOffset)
    {
        byte[] input = GC.AllocateArray<byte>(256, pinned: true);
        SszWitnessCodes[] codes = InInput(input, (firstOffset, 40), (secondOffset, 4));
        byte[][] expected = Contents(codes);
        byte[] untouched = input.ToArray();

        int taken = WitnessCode.MoveToFront(codes, input.AsSpan(0, consumedLength));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken, Is.Zero);
            Assert.That(input, Is.EqualTo(untouched));
            for (int i = 0; i < codes.Length; i++)
            {
                Assert.That(ExecutableCodeMemory.TryGetExecutionBuffer(codes[i].Bytes.Code, out _), Is.True);
                Assert.That(codes[i].Bytes.Code.ToArray(), Is.EqualTo(expected[i]));
            }
        }
    }

    private static SszWitnessCodes[] InInput(byte[] input, params (int Offset, int Length)[] layout)
    {
        input.AsSpan().Fill(0xff);
        SszWitnessCodes[] codes = new SszWitnessCodes[layout.Length];
        for (int i = 0; i < layout.Length; i++)
        {
            Span<byte> code = input.AsSpan(layout[i].Offset, layout[i].Length);
            for (int j = 0; j < code.Length; j++)
                code[j] = (byte)(16 * (i + 1) + j);

            codes[i] = new() { Bytes = new WitnessCode(ExecutableCodeMemory.Wrap(code)) };
        }

        return codes;
    }

    private static byte[][] Contents(SszWitnessCodes[] codes) => codes.Select(static c => c.Bytes.Code.ToArray()).ToArray();

    private static void AssertPlacedAt(SszWitnessCodes code, byte[] input, int offset, byte[] expected)
    {
        ReadOnlyMemory<byte> placed = code.Bytes.Code;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExecutableCodeMemory.IsPlaced(placed), Is.True);
            Assert.That(Unsafe.AreSame(ref MemoryMarshal.GetReference(placed.Span), ref input[offset]), Is.True);
            Assert.That(placed.ToArray(), Is.EqualTo(expected));
            Assert.That(input.AsSpan(offset + expected.Length, Padding).ToArray(), Is.All.EqualTo(0));
        }
    }
}
