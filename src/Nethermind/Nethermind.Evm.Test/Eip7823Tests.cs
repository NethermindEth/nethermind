// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip7823Tests
{
    private const int SizeOfUInt256 = UInt256.Len * sizeof(ulong);
    private const uint ModExpMaxInputSizeEip7823 = ModExpPrecompile.ModExpMaxInputSizeEip7823;
    private const uint ModExpMaxInputSizeEip7823PlusOne = ModExpPrecompile.ModExpMaxInputSizeEip7823 + 1;

    [TestCase(true, true, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823)]
    [TestCase(false, true, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823)]
    [TestCase(true, false, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823)]
    [TestCase(false, true, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823)]
    [TestCase(true, false, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823)]
    [TestCase(false, true, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823)]
    [TestCase(true, false, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823PlusOne)]
    [TestCase(false, true, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823PlusOne)]
    [TestCase(true, false, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823)]
    [TestCase(false, true, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823)]
    [TestCase(true, false, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823PlusOne)]
    [TestCase(false, true, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823PlusOne)]
    [TestCase(true, false, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823PlusOne)]
    [TestCase(false, true, ModExpMaxInputSizeEip7823, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823PlusOne)]
    [TestCase(true, false, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823PlusOne)]
    [TestCase(false, true, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823PlusOne, ModExpMaxInputSizeEip7823PlusOne)]
    public void TestEip7823(bool isEip7823Enabled, bool success, uint inputBaseLength, uint inputExpLength, uint inputModulusLength)
    {
        IReleaseSpec spec = new Prague() { IsEip7823Enabled = isEip7823Enabled };

        byte[] input = new byte[(SizeOfUInt256 + ModExpMaxInputSizeEip7823PlusOne) * 3];
        ref byte inputRef = ref MemoryMarshal.GetArrayDataReference(input);

        ref uint baseLength = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref inputRef, SizeOfUInt256 - sizeof(uint)));
        ref uint expLength = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref inputRef, 2 * SizeOfUInt256 - sizeof(uint)));
        ref uint modulusLength = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref inputRef, 3 * SizeOfUInt256 - sizeof(uint)));

        baseLength = BinaryPrimitives.ReverseEndianness(inputBaseLength);
        expLength = BinaryPrimitives.ReverseEndianness(inputExpLength);
        modulusLength = BinaryPrimitives.ReverseEndianness(inputModulusLength);

        Assert.That(TestSuccess(input, spec), Is.EqualTo(success));

        long gas = TestGas(input, spec);
        if (success)
        {
            Assert.That(gas, Is.LessThan(long.MaxValue));
        }
        else
        {
            Assert.That(gas, Is.EqualTo(long.MaxValue));
        }
    }

    private static bool TestSuccess(byte[] input, IReleaseSpec spec)
    {
        (_, bool result) = ModExpPrecompile.Instance.Run(input, spec);
        return result;
    }

    private static long TestGas(byte[] input, IReleaseSpec spec)
        => ModExpPrecompile.Instance.DataGasCost(input, spec);
}
