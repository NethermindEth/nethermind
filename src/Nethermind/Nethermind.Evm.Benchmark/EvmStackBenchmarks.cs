// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
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

        [GlobalSetup]
        public void GlobalSetup()
        {
            _stack = new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength * 32) * 1024];
        }

        [Benchmark(OperationsPerInvoke = 4)]
        [ArgumentsSource(nameof(ValueSource))]
        public UInt256 Uint256(UInt256 v)
        {
            EvmStack<VirtualMachine.NotTracing> stack = new(_stack.AsSpan(), 0, NullTxTracer.Instance);

            stack.PushUInt256(in v);
            stack.PopUInt256(out UInt256 value);

            stack.PushUInt256(in value);
            stack.PopUInt256(out value);

            stack.PushUInt256(in value);
            stack.PopUInt256(out value);

            stack.PushUInt256(in value);
            stack.PopUInt256(out value);

            return value;
        }

        [Benchmark(OperationsPerInvoke = 4)]
        [ArgumentsSource(nameof(ValueSource))]
        public Int256.Int256 Int256(UInt256 v)
        {
            EvmStack<VirtualMachine.NotTracing> stack = new(_stack.AsSpan(), 0, NullTxTracer.Instance);

            stack.PushSignedInt256(new Int256.Int256(v));
            stack.PopSignedInt256(out Int256.Int256 value);

            stack.PushSignedInt256(value);
            stack.PopSignedInt256(out value);

            stack.PushSignedInt256(value);
            stack.PopSignedInt256(out value);

            stack.PushSignedInt256(value);
            stack.PopSignedInt256(out value);

            return value;
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public byte Byte()
        {
            EvmStack<VirtualMachine.NotTracing> stack = new(_stack.AsSpan(), 0, NullTxTracer.Instance);

            byte b = 1;

            stack.PushByte(b);
            b = stack.PopByte();

            stack.PushByte(b);
            b = stack.PopByte();

            stack.PushByte(b);
            b = stack.PopByte();

            stack.PushByte(b);
            b = stack.PopByte();

            return b;
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void PushZero()
        {
            EvmStack<VirtualMachine.NotTracing> stack = new(_stack.AsSpan(), 0, NullTxTracer.Instance);

            stack.PushZero();
            stack.PushZero();
            stack.PushZero();
            stack.PushZero();
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void PushOne()
        {
            EvmStack<VirtualMachine.NotTracing> stack = new(_stack.AsSpan(), 0, NullTxTracer.Instance);

            stack.PushOne();
            stack.PushOne();
            stack.PushOne();
            stack.PushOne();
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void Swap()
        {
            EvmStack<VirtualMachine.NotTracing> stack = new(_stack.AsSpan(), 2, NullTxTracer.Instance);

            stack.Swap(2);
            stack.Swap(2);
            stack.Swap(2);
            stack.Swap(2);
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void Dup()
        {
            EvmStack<VirtualMachine.NotTracing> stack = new(_stack.AsSpan(), 1, NullTxTracer.Instance);

            stack.Dup(1);
            stack.Dup(1);
            stack.Dup(1);
            stack.Dup(1);
        }
    }
}
