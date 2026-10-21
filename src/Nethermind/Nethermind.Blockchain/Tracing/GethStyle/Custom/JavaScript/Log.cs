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
        public Stack stack { get; set; } = new();
        public Memory memory { get; set; } = new();
        public Contract contract { get; set; } = null!;
        public long pc { get; set; }

        public ulong gas { get; set; }
        public ulong? gasCost { get; set; }
        public int depth { get; set; }
        public long refund { get; set; }
        public string? error { get; set; }

        public ulong getPC() => (ulong)pc;
        public ulong getGas() => gas;
        public ulong getCost() => gasCost ?? 0;
        public int getDepth() => depth;
        public ulong getRefund() => (ulong)refund;
        public dynamic getError() => !string.IsNullOrEmpty(error) ? error : Undefined.Value;

        public sealed class Opcode
        {
            // Shared per instruction so each step hands the script an existing host object rather than a new box.
            private static readonly Opcode[] All = CreateAll();

            private Opcode(Instruction value) => Value = value;

            public Instruction Value { get; }

            internal static Opcode Of(Instruction value) => All[(byte)value];

            private static Opcode[] CreateAll()
            {
                Opcode[] all = new Opcode[byte.MaxValue + 1];
                for (int i = 0; i < all.Length; i++)
                {
                    all[i] = new Opcode((Instruction)i);
                }
                return all;
            }

            public int toNumber() => (int)Value;
            public string toString() => OpcodeJsonNames.GetName(Value);
            public bool isPush() => Value is >= Instruction.PUSH0 and <= Instruction.PUSH32;
        }

        public class Stack
        {
            internal TraceStack Items;
            internal Engine? InputErrorEngine { get; set; }

            public int length() => Items.Count;
            public IJavaScriptObject peek(int index)
            {
                if (InputErrorEngine is not null && (index < 0 || index >= Items.Count))
                {
                    InputErrorEngine.AbortInput($"tracer accessed out of bound stack: size {Items.Count}, index {index}");
                    return null!;
                }
                return new BigInteger(Items[^(index + 1)].Span, true, true).ToBigInteger();
            }
        }

        public class Memory
        {
            public TraceMemory MemoryTrace;
            internal Engine? InputErrorEngine { get; set; }

            public int length() => (int)MemoryTrace.Size;

            public ITypedArray<byte> slice(long start, long end)
            {
                if (InputErrorEngine is not null)
                {
                    if (start == end) return Array.Empty<byte>().ToTypedScriptArray();
                    if (start < 0 || end < start)
                    {
                        InputErrorEngine.AbortInput($"tracer accessed out of bound memory: offset {start}, end {end}");
                        return null!;
                    }
                    if ((ulong)end > MemoryTrace.Size && (ulong)end - MemoryTrace.Size > MemorySizes.MiB)
                    {
                        InputErrorEngine.AbortInput($"reached limit for padding memory slice: {(ulong)end - MemoryTrace.Size}");
                        return null!;
                    }
                    if (end > Array.MaxLength)
                    {
                        InputErrorEngine.AbortInput($"tracer accessed out of bound memory: offset {start}, end {end}");
                        return null!;
                    }
                }
                if (start < 0 || end < start || end > Array.MaxLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(start), $"tracer accessed out of bound memory: offset {start}, end {end}");
                }

                int length = (int)(end - start);
                return MemoryTrace.Slice((int)start, length, limit: InputErrorEngine is null)
                    .ToArray()
                    .ToTypedScriptArray();
            }

            public IJavaScriptObject getUint(int offset)
            {
                if (InputErrorEngine is not null && (offset < 0 || (ulong)offset + EvmPooledMemory.WordSize > MemoryTrace.Size))
                {
                    InputErrorEngine.AbortInput($"tracer accessed out of bound memory: available {MemoryTrace.Size}, offset {offset}, size {EvmPooledMemory.WordSize}");
                    return null!;
                }
                return (InputErrorEngine is null
                    ? MemoryTrace.GetUint(offset)
                    : new BigInteger(MemoryTrace.Slice(offset, EvmPooledMemory.WordSize, limit: false), true, true)).ToBigInteger();
            }
        }

        public sealed class Contract(Address caller, Address address, UInt256 value, ReadOnlyMemory<byte>? input)
        {
            private readonly UInt256 _value = value;
            private readonly Address _address = address;
            private readonly ReadOnlyMemory<byte>? _input = input;
            private IJavaScriptObject? _valueConverted;
            public Address Caller { get; } = caller;

            public ITypedArray<byte> getAddress() => _address.Bytes.ToArray().ToTypedScriptArray();
            public ITypedArray<byte> getCaller() => Caller.Bytes.ToArray().ToTypedScriptArray();
            public object getInput() => _input?.ToArray().ToTypedScriptArray() ?? (object)Undefined.Value;

            public IJavaScriptObject getValue() => _valueConverted ??= _value.ToBigInteger();
        }
    }
}
