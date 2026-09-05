// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark
{
    public class EvmStackBenchmarks
    {
        public IEnumerable<UInt256> ValueSource => new[]
        {
            UInt256.Parse("125124123718263172357123"),
            UInt256.Parse("0"),
            UInt256.MaxValue
        };

        private byte[] _stack;
        private EvmWord _word;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _stack = new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength * 32) * 1024];
            _word = Vector256.Create(
                0x0706050403020100ul,
                0x0f0e0d0c0b0a0908ul,
                0x1716151413121110ul,
                0x1f1e1d1c1b1a1918ul).AsByte();
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public EvmWord ByteSwap()
        {
            EvmWord word = _word;
            word = word.ByteSwap();
            word = word.ByteSwap();
            word = word.ByteSwap();
            return word.ByteSwap();
        }

        [Benchmark(OperationsPerInvoke = 4)]
        [ArgumentsSource(nameof(ValueSource))]
        public UInt256 Uint256(UInt256 v)
        {
            EvmStack stack = new(0, NullTxTracer.Instance, ref MemoryMarshal.GetArrayDataReference(_stack), default);

            stack.PushUInt256<OffFlag>(in v);
            stack.PopUInt256(out UInt256 value);

            stack.PushUInt256<OffFlag>(in value);
            stack.PopUInt256(out value);

            stack.PushUInt256<OffFlag>(in value);
            stack.PopUInt256(out value);

            stack.PushUInt256<OffFlag>(in value);
            stack.PopUInt256(out value);

            return value;
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public byte Byte()
        {
            EvmStack stack = new(0, NullTxTracer.Instance, ref MemoryMarshal.GetArrayDataReference(_stack), default);

            int b = 1;

            stack.PushByte<OffFlag>((byte)b);
            b = stack.PopByte();

            stack.PushByte<OffFlag>((byte)b);
            b = stack.PopByte();

            stack.PushByte<OffFlag>((byte)b);
            b = stack.PopByte();

            stack.PushByte<OffFlag>((byte)b);
            b = stack.PopByte();

            return (byte)b;
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void PushZero()
        {
            EvmStack stack = new(0, NullTxTracer.Instance, ref MemoryMarshal.GetArrayDataReference(_stack), default);

            stack.PushZero<OffFlag>();
            stack.PushZero<OffFlag>();
            stack.PushZero<OffFlag>();
            stack.PushZero<OffFlag>();
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void PushOne()
        {
            EvmStack stack = new(0, NullTxTracer.Instance, ref MemoryMarshal.GetArrayDataReference(_stack), default);

            stack.PushOne<OffFlag>();
            stack.PushOne<OffFlag>();
            stack.PushOne<OffFlag>();
            stack.PushOne<OffFlag>();
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void PushUInt32()
        {
            EvmStack stack = new(0, NullTxTracer.Instance, ref MemoryMarshal.GetArrayDataReference(_stack), default);

            stack.PushUInt32<OffFlag>(0x10203040);
            stack.PushUInt32<OffFlag>(0x50607080);
            stack.PushUInt32<OffFlag>(0x90A0B0C0);
            stack.PushUInt32<OffFlag>(0xD0E0F000);
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void PushUInt64()
        {
            EvmStack stack = new(0, NullTxTracer.Instance, ref MemoryMarshal.GetArrayDataReference(_stack), default);

            stack.PushUInt64<OffFlag>(0x1020304050607080);
            stack.PushUInt64<OffFlag>(0x90A0B0C0D0E0F000);
            stack.PushUInt64<OffFlag>(0x0123456789ABCDEF);
            stack.PushUInt64<OffFlag>(0xFEDCBA9876543210);
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void Swap()
        {
            EvmStack stack = new(0, NullTxTracer.Instance, ref MemoryMarshal.GetArrayDataReference(_stack), default);

            stack.Swap<OffFlag, OnFlag>(2);
            stack.Swap<OffFlag, OnFlag>(2);
            stack.Swap<OffFlag, OnFlag>(2);
            stack.Swap<OffFlag, OnFlag>(2);
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void Dup()
        {
            EvmStack stack = new(1, NullTxTracer.Instance, ref MemoryMarshal.GetArrayDataReference(_stack), default);

            stack.Dup<OffFlag>(1);
            stack.Dup<OffFlag>(1);
            stack.Dup<OffFlag>(1);
            stack.Dup<OffFlag>(1);
        }
    }
}
