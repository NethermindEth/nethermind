// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Tests for the zero bytes guest dispatch reads past the end of the code instead of testing the program counter.</summary>
public class GuestCodePaddingTests
{
    [Test]
    public void Code_is_followed_by_zero_padding([Values(1, 2, 32, 33, 100, 4096)] int length)
    {
        // PUSH32 in the last byte reads 32 immediates, and dispatch then reads the next opcode and the byte after it.
        const int padding = 34;

        // The buffer goes on past the code with bytes the padding must not inherit.
        byte[] buffer = new byte[length + padding + 8];
        Array.Fill(buffer, (byte)Instruction.JUMPDEST);

        CodeInfo codeInfo = new(buffer.AsMemory(0, length));

        ReadOnlySpan<byte> execution = codeInfo.ExecutionCodeSpan;
        ReadOnlySpan<byte> afterCode = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetReference(execution), execution.Length), padding);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.ToArray(), Is.EqualTo(buffer[..length]));
            Assert.That(codeInfo.CodeSpan.ToArray(), Is.EqualTo(buffer[..length]));
            Assert.That(afterCode.IndexOfAnyExcept((byte)Instruction.STOP), Is.EqualTo(-1));
        }
    }
}
