// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
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
            EvmStack stack = new(0, NullTxTracer.Instance, _stack.AsSpan());

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
            EvmStack stack = new(0, NullTxTracer.Instance, _stack.AsSpan());

            byte b = 1;

            stack.PushByte<OffFlag>(b);
            b = stack.PopByte();

            stack.PushByte<OffFlag>(b);
            b = stack.PopByte();

            stack.PushByte<OffFlag>(b);
            b = stack.PopByte();

            stack.PushByte<OffFlag>(b);
            b = stack.PopByte();

            return b;
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void PushZero()
        {
            EvmStack stack = new(0, NullTxTracer.Instance, _stack.AsSpan());

            stack.PushZero<OffFlag>();
            stack.PushZero<OffFlag>();
            stack.PushZero<OffFlag>();
            stack.PushZero<OffFlag>();
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void PushOne()
        {
            EvmStack stack = new(0, NullTxTracer.Instance, _stack.AsSpan());

            stack.PushOne<OffFlag>();
            stack.PushOne<OffFlag>();
            stack.PushOne<OffFlag>();
            stack.PushOne<OffFlag>();
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void Swap()
        {
            EvmStack stack = new(0, NullTxTracer.Instance, _stack.AsSpan());

            stack.Swap<OffFlag>(2);
            stack.Swap<OffFlag>(2);
            stack.Swap<OffFlag>(2);
            stack.Swap<OffFlag>(2);
        }

        [Benchmark(OperationsPerInvoke = 4)]
        public void Dup()
        {
            EvmStack stack = new(1, NullTxTracer.Instance, _stack.AsSpan());

            stack.Dup<OffFlag>(1);
            stack.Dup<OffFlag>(1);
            stack.Dup<OffFlag>(1);
            stack.Dup<OffFlag>(1);
        }
    }
}
