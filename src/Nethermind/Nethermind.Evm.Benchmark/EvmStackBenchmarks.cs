// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark
{
    [MemoryDiagnoser]
    public class EvmStackBenchmarks
    {
        [Params(16, 128, 512)]
        public int Operations { get; set; }

        [Params(1, 8, 16)]
        public int Depth { get; set; }

        private byte[] _stack = null!;
        private UInt256[] _values = null!;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _stack = new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength) * EvmStack.WordSize];
            _values =
            [
                UInt256.Zero,
                UInt256.One,
                UInt256.Parse("125124123718263172357123"),
                UInt256.MaxValue
            ];
        }

        [Benchmark]
        public UInt256 PushPopUInt256Loop()
        {
            EvmStack stack = CreateStack();
            UInt256 value = UInt256.Zero;

            for (int i = 0; i < Operations; i++)
            {
                UInt256 input = _values[i & 3];
                stack.PushUInt256<OffFlag>(in input);
                stack.PopUInt256(out value);
            }

            return value;
        }

        [Benchmark]
        public byte PushPopByteLoop()
        {
            EvmStack stack = CreateStack();
            byte value = 0x1f;
            for (int i = 0; i < Operations; i++)
            {
                stack.PushByte<OffFlag>(value);
                value = stack.PopByte();
            }

            return value;
        }

        [Benchmark]
        public int DupDeep()
        {
            EvmStack stack = CreateFilledStack(Depth + 1);
            for (int i = 0; i < Operations; i++)
            {
                stack.Dup<OffFlag>(Depth);
            }

            return stack.Head;
        }

        [Benchmark]
        public int SwapDeep()
        {
            EvmStack stack = CreateFilledStack(Depth + 1);
            for (int i = 0; i < Operations; i++)
            {
                stack.Swap<OffFlag>(Depth);
            }

            return stack.Head;
        }

        [Benchmark]
        public int MixedStackPattern()
        {
            EvmStack stack = CreateFilledStack(Math.Max(Depth + 1, 4));
            for (int i = 0; i < Operations; i++)
            {
                UInt256 input = _values[i & 3];
                stack.PushUInt256<OffFlag>(in input);
                stack.Dup<OffFlag>(Depth);
                stack.Swap<OffFlag>(Depth);
                stack.PopLimbo();
                stack.PopLimbo();
            }

            return stack.Head;
        }

        private EvmStack CreateStack() => new(0, NullTxTracer.Instance, _stack.AsSpan());

        private EvmStack CreateFilledStack(int size)
        {
            EvmStack stack = CreateStack();
            for (int i = 0; i < size; i++)
            {
                UInt256 value = _values[i & 3];
                stack.PushUInt256<OffFlag>(in value);
            }

            return stack;
        }
    }
}
