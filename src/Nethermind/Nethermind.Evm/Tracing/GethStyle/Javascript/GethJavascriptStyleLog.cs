// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Int256;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.ClearScript;
using Nethermind.Core.Extensions;


// ReSharper disable InconsistentNaming

namespace Nethermind.Evm.Tracing.GethStyle.Javascript
{
    public class GethJavascriptStyleLog
    {
        public Opcode? op { get; set; }
        public Stack? stack { get; set; }
        public Memory? memory { get; set; }
        public Contract? contract { get; set; }
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
            public BigInteger peek(int index) => new(_items[^(index + 1)].Span);
        }

        public readonly struct Memory
        {
            private readonly V8ScriptEngine _engine;
            private readonly List<byte> _memory;

            public Memory(V8ScriptEngine engine, List<byte> memory)
            {
                _engine = engine;
                _memory = memory;
            }

            public int length() => _memory.Count;

            public ScriptObject slice(BigInteger start, BigInteger end)
            {
                if (start < 0 || end < start )
                {
                    throw new ArgumentOutOfRangeException(nameof(start), $"tracer accessed out of bound memory: offset {start}, end {end}");
                }

                // TODO: Pad memory
                if (end > _memory.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(end), "Invalid end.");
                }

                int length = (int)(end - start);
                Span<byte> slice = length == 0
                    ? Span<byte>.Empty
                    : CollectionsMarshal.AsSpan(_memory).Slice((int)start, length);
                return slice.ToArray().ToScriptArray(_engine);
            }

            public BigInteger getUint(int offset)
            {
                if (offset < 0 || offset + EvmPooledMemory.WordSize > _memory.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset), $"tracer accessed out of bound memory: available {_memory.Count}, offset {offset}, size {EvmPooledMemory.WordSize}");
                }

                ReadOnlySpan<byte> byteArray = CollectionsMarshal.AsSpan(_memory).Slice(offset, EvmPooledMemory.WordSize);
                return new BigInteger(byteArray);
            }
        }

        public struct Contract
        {
            private readonly V8ScriptEngine _engine;
            private readonly UInt256 _value;
            private readonly Address? _address;
            private readonly ReadOnlyMemory<byte> _input;
            private ScriptObject? _callerConverted;
            private ScriptObject? _addressConverted;
            private ScriptObject? _inputConverted;
            private readonly Address _caller;


            public Contract(V8ScriptEngine engine, Address caller, Address? address, UInt256 value, ReadOnlyMemory<byte> input)
            {
                _engine = engine;
                _caller = caller;
                _address = address;
                _value = value;
                _input = input;
            }

            public ScriptObject? getAddress() => _addressConverted ??= _address?.Bytes.ToScriptArray(_engine);
            public ScriptObject getCaller() => _callerConverted ??= _caller.Bytes.ToScriptArray(_engine);
            public ScriptObject getInput() => _inputConverted ??= _input.ToArray().ToScriptArray(_engine);
            public BigInteger getValue() => (BigInteger)_value;
        }
    }
}
