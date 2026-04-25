// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;
using System.Numerics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

// ReSharper disable InconsistentNaming

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript
{
    public class Log
    {
        public Opcode? op { get; set; }
        public Stack stack { get; set; }
        public Memory memory { get; set; } = new();
        public Contract contract { get; set; }
        public long pc { get; set; }

        public long gas { get; set; }
        public long? gasCost { get; set; }
        public int depth { get; set; }
        public long refund { get; set; }
        public string? error { get; set; }

        public ulong getPC() => (ulong)pc;
        public ulong getGas() => (ulong)gas;
        public ulong getCost() => (ulong)(gasCost ?? 0);
        public int getDepth() => depth;
        public ulong getRefund() => (ulong)refund;
        public dynamic getError() => !string.IsNullOrEmpty(error) ? error : Undefined.Value;

        public readonly struct Opcode(Instruction value)
        {
            public Instruction Value { get; } = value;

            public int toNumber() => (int)Value;
            public string? toString() => Enum.GetName(Value);
            public bool isPush() => Value is >= Instruction.PUSH0 and <= Instruction.PUSH32;
        }

        public readonly struct Stack(TraceStack items)
        {
            private readonly TraceStack _items = items;

            public int length() => _items.Count;
            public IJavaScriptObject peek(int index) => new BigInteger(_items[^(index + 1)].Span, true, true).ToBigInteger();
        }

        public class Memory
        {
            public TraceMemory MemoryTrace;

            public int length() => (int)MemoryTrace.Size;

            public ITypedArray<byte> slice(long start, long end)
            {
                if (start < 0 || end < start || end > Array.MaxLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(start), $"tracer accessed out of bound memory: offset {start}, end {end}");
                }

                int length = (int)(end - start);
                return MemoryTrace.Slice((int)start, length)
                    .ToArray()
                    .ToTypedScriptArray();
            }

            public IJavaScriptObject getUint(int offset) => MemoryTrace.GetUint(offset).ToBigInteger();
        }

        public struct Contract(Address caller, Address address, UInt256 value, ReadOnlyMemory<byte>? input)
        {
            private readonly UInt256 _value = value;
            private readonly Address _address = address;
            private readonly ReadOnlyMemory<byte>? _input = input;
            private ITypedArray<byte>? _callerConverted;
            private ITypedArray<byte>? _addressConverted;
            private ITypedArray<byte>? _inputConverted;
            private IJavaScriptObject? _valueConverted;
            public Address Caller { get; } = caller;

            public ITypedArray<byte> getAddress() => _addressConverted ??= _address.Bytes.ToTypedScriptArray();
            public ITypedArray<byte> getCaller() => _callerConverted ??= Caller.Bytes.ToTypedScriptArray();
            public object getInput() => (_inputConverted ??= _input?.ToArray().ToTypedScriptArray()) ?? (object)Undefined.Value;

            public IJavaScriptObject getValue() => _valueConverted ??= _value.ToBigInteger();
        }
    }
}
