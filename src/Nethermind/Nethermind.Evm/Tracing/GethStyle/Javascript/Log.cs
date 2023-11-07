// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;

// ReSharper disable InconsistentNaming

namespace Nethermind.Evm.Tracing.GethStyle.Javascript
{
    public class Log
    {
        public Opcode? op { get; set; }
        public Stack stack { get; set; }
        public Memory memory { get; set; } = new();
        public Contract contract { get; set; }
        public long pc { get; set; }

        public long gas { get; set; }
        public long gasCost { get; set; }
        public int depth { get; set; }
        public long refund { get; set; }
        public string? error { get; set; }

        public ulong getPC() => (ulong)pc;
        public ulong getGas() => (ulong)gas;
        public ulong getCost() => (ulong)gasCost;
        public int getDepth() => depth;
        public ulong getRefund() => (ulong)refund;
        public dynamic getError() => !string.IsNullOrEmpty(error) ? error : Undefined.Value;

        public readonly struct Opcode
        {
            private readonly Instruction _value;
            public Opcode(Instruction value) => _value = value;
            public int toNumber() => (int)_value;
            public string? toString() => _value.GetName();
            public bool isPush() => _value is >= Instruction.PUSH0 and <= Instruction.PUSH32;
        }

        public readonly struct Stack
        {
            private readonly TraceStack _items;
            public Stack(TraceStack items) => _items = items;
            public int length() => _items.Count;
            public BigInteger peek(int index) => new(_items[^(index + 1)].Span, true, true);
        }

        public class Memory
        {
            public TraceMemory MemoryTrace;

            public int length() => (int)MemoryTrace.Size;

            public ITypedArray<byte> slice(int start, int end)
            {
                if (start < 0 || end < start)
                {
                    throw new ArgumentOutOfRangeException(nameof(start), $"tracer accessed out of bound memory: offset {start}, end {end}");
                }

                int length = end - start;
                return MemoryTrace.Slice(start, length)
                    .ToArray()
                    .ToScriptArray();
            }

            public BigInteger getUint(int offset) => MemoryTrace.GetUint(offset);
        }

        public struct Contract
        {
            private readonly UInt256 _value;
            private readonly Address _address;
            private readonly ReadOnlyMemory<byte> _input;
            private ITypedArray<byte>? _callerConverted;
            private ITypedArray<byte>? _addressConverted;
            private ITypedArray<byte>? _inputConverted;
            private readonly Address _caller;


            public Contract(Address caller, Address address, UInt256 value, ReadOnlyMemory<byte> input)
            {
                _caller = caller;
                _address = address;
                _value = value;
                _input = input;
            }

            public ITypedArray<byte> getAddress() => _addressConverted ??= _address.Bytes.ToScriptArray();
            public ITypedArray<byte> getCaller() => _callerConverted ??= _caller.Bytes.ToScriptArray();
            public ITypedArray<byte> getInput() => _inputConverted ??= _input.ToArray().ToScriptArray();
            public BigInteger getValue() => (BigInteger)_value;
        }
    }
}
