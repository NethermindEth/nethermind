// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Tests for the buffers that guest creation frames at one call depth share.</summary>
/// <remarks>The buffers are process-wide, so each test takes call depths of its own and none runs in parallel.</remarks>
[NonParallelizable]
public class GuestInitCodeTests
{
    private const byte JUMPDEST = (byte)Instruction.JUMPDEST;
    private const byte PUSH32 = (byte)Instruction.PUSH32;

    [Test]
    public void Init_code_at_one_call_depth_reuses_its_buffers()
    {
        CodeInfo first = CodeInfo.ForInitCode(Filled(100, JUMPDEST), callDepth: 1);
        long[] firstBitmap = first.IncrementalJumpBitmap;
        CodeInfo second = CodeInfo.ForInitCode(Filled(80, JUMPDEST), callDepth: 1);
        CodeInfo deeper = CodeInfo.ForInitCode(Filled(80, JUMPDEST), callDepth: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Unsafe.AreSame(in First(second), in First(first)), Is.True, "the code buffer");
            Assert.That(second.IncrementalJumpBitmap, Is.SameAs(firstBitmap), "the jump bitmap");
            Assert.That(Unsafe.AreSame(in First(deeper), in First(first)), Is.False, "another call depth's code buffer");
            Assert.That(deeper.IncrementalJumpBitmap, Is.Not.SameAs(firstBitmap), "another call depth's jump bitmap");
        }
    }

    [Test]
    public void Init_code_in_a_reused_buffer_is_padded_and_analyzed_afresh()
    {
        const int callDepth = 3;
        CodeInfo previous = CodeInfo.ForInitCode(Filled(100, JUMPDEST), callDepth);
        for (int destination = 0; destination < previous.CodeLength; destination++)
        {
            Assert.That(IsJumpDestination(previous, destination), Is.True);
        }

        // The previous init code left a JUMPDEST at every position: the PUSH32 data and the padding must not keep them.
        byte[] code = new byte[60];
        code[0] = PUSH32;
        code[10] = JUMPDEST;
        code[40] = JUMPDEST;
        CodeInfo codeInfo = CodeInfo.ForInitCode(code, callDepth);

        ReadOnlySpan<byte> execution = codeInfo.ExecutionCodeSpan;
        ReadOnlySpan<byte> padding = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetReference(execution), execution.Length), CodeInfo.ExecutionPadding);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.ToArray(), Is.EqualTo(code));
            Assert.That(padding.IndexOfAnyExcept((byte)Instruction.STOP), Is.EqualTo(-1), "the padding");
            Assert.That(IsJumpDestination(codeInfo, 10), Is.False, "a JUMPDEST in PUSH32 data");
            Assert.That(IsJumpDestination(codeInfo, 39), Is.False, "a STOP");
            Assert.That(IsJumpDestination(codeInfo, 40), Is.True, "a JUMPDEST");
        }
    }

    [Test]
    public void Longer_init_code_grows_the_buffers_of_its_call_depth()
    {
        const int callDepth = 4;
        CodeInfo shorter = CodeInfo.ForInitCode(Filled(64, JUMPDEST), callDepth);
        long[] shorterBitmap = shorter.IncrementalJumpBitmap;
        byte[] code = Filled(1000, JUMPDEST);
        CodeInfo longer = CodeInfo.ForInitCode(code, callDepth);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(longer.ExecutionCodeSpan.ToArray(), Is.EqualTo(code));
            Assert.That(longer.IncrementalJumpBitmap, Is.Not.SameAs(shorterBitmap));
            Assert.That(IsJumpDestination(longer, 999), Is.True);
        }
    }

    [Test]
    public void Empty_init_code_is_empty() => Assert.That(CodeInfo.ForInitCode(ReadOnlyMemory<byte>.Empty, callDepth: 5).IsEmpty, Is.True);

    [Test]
    public void Init_code_is_borrowed_from_memory_until_its_one_copy([Values] bool inlineMemory)
    {
        const int callDepth = 6;
        byte[] code = Filled(200, JUMPDEST);
        UInt256 location = 64;
        UInt256 length = (UInt256)code.Length;
        using EvmFrameMemory frameMemory = new();
        EvmPooledMemory memory = inlineMemory ? new(frameMemory) : default;
        try
        {
            bool saved = memory.TrySave(in location, code);
            bool loaded = CodeInfo.TryLoadInitCode(ref memory, in location, in length, callDepth, out ReadOnlyMemory<byte> initCode);
            CodeInfo codeInfo = CodeInfo.ForInitCode(initCode, callDepth);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(saved && loaded, Is.True);
                Assert.That(Unsafe.AreSame(in MemoryMarshal.GetReference(initCode.Span), in MemoryMarshal.GetReference(memory.Inspect(in location, in length).Span)), Is.True, "borrowed");
                Assert.That(codeInfo.ExecutionCodeSpan.ToArray(), Is.EqualTo(code));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Test]
    public void Init_code_loaded_past_the_written_memory_reads_zeros_over_longer_init_code()
    {
        const int callDepth = 7;
        CodeInfo.ForInitCode(Filled(300, JUMPDEST), callDepth);

        byte[] written = Filled(32, PUSH32);
        UInt256 length = 100;
        EvmPooledMemory memory = default;
        try
        {
            bool saved = memory.TrySave(UInt256.Zero, written);
            // Expands the memory over the init code, as the creation's charge does.
            memory.CalculateMemoryCost(UInt256.Zero, in length, out bool outOfGas);
            bool loaded = CodeInfo.TryLoadInitCode(ref memory, UInt256.Zero, in length, callDepth, out ReadOnlyMemory<byte> initCode);
            CodeInfo codeInfo = CodeInfo.ForInitCode(initCode, callDepth);

            ReadOnlySpan<byte> execution = codeInfo.ExecutionCodeSpan;
            ReadOnlySpan<byte> padding = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetReference(execution), execution.Length), CodeInfo.ExecutionPadding);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(saved && !outOfGas && loaded, Is.True);
                Assert.That(execution.Length, Is.EqualTo(100));
                Assert.That(execution[..32].ToArray(), Is.EqualTo(written));
                Assert.That(execution[32..].IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "the unwritten memory");
                Assert.That(padding.IndexOfAnyExcept((byte)Instruction.STOP), Is.EqualTo(-1), "the padding");
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    /// <summary>The bit test guest dispatch takes, falling back to the analysis of a destination not decided yet.</summary>
    private static bool IsJumpDestination(CodeInfo codeInfo, int destination)
    {
        long[] bitmap = codeInfo.IncrementalJumpBitmap;
        return JumpDestinationAnalyzer.IsJumpDestination(bitmap, destination) || codeInfo.AnalyzeJump(destination, bitmap, codeInfo.CodeSpan);
    }

    private static ref readonly byte First(CodeInfo codeInfo) => ref MemoryMarshal.GetReference(codeInfo.ExecutionCodeSpan);

    private static byte[] Filled(int length, byte value)
    {
        byte[] code = new byte[length];
        Array.Fill(code, value);
        return code;
    }
}
