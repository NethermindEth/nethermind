// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Microsoft.ClearScript.V8;
using Nethermind.Int256;
// ReSharper disable InconsistentNaming

namespace Nethermind.Evm.Tracing.GethStyle
{
    public class GethJavascriptStyleLog
    {
        private readonly V8ScriptEngine _engine;

        public GethJavascriptStyleLog(V8ScriptEngine engine)
        {
            _engine = engine;
        }

        public long? pc { get; set; }
        public OpcodeString? op { get; set; }
        public long? gas { get; set; }
        public long? gasCost { get; set; }
        public int? depth { get; set; }
        public long? refund { get; set; }
        public string? error { get; set; }
        public Contract? contract { get; set; }
        public JSStack? stack { get; set; }
        public JSMemory? memory { get; set; }
        public long? getPC() => pc;

        public long? getGas() => gas;

        public long? getCost() => gasCost;

        public int? getDepth() => depth;

        public long? getRefund() => refund;

        public string? getError() //needs looking into
            => !string.IsNullOrEmpty(error) ? error : null;

        public class OpcodeString
        {
            private readonly Instruction _value;
            public OpcodeString(Instruction value) => _value = value;
            public string? toNumber() => _value.GetHex();
            public string? toString() => _value.GetName();
            public bool? isPush() => _value is >= Instruction.PUSH0 and <= Instruction.PUSH32;
        }

        public class JSStack
        {
            private readonly List<string> _items;

            public JSStack(List<string> items) => _items = items;

            public string? length() => _items.Count.ToString();

            public int? getCount() => _items.Count;

            public string? peek(int index)
            {
                int topIndex = _items.Count - 1 - index;
                return topIndex >= 0 && topIndex < _items.Count ? _items[topIndex] : null;
            }
            public string? getItem(int index) => index >= 0 && index < _items.Count ? _items[index] : null;
        }

        public class JSMemory
        {
            private readonly List<string> _memoryTrace;

            public JSMemory(List<string> memoryTrace) => _memoryTrace = memoryTrace;
            public int? getCount() => _memoryTrace.Count;

            public string? getItem(int index) => index >= 0 && index < _memoryTrace.Count ? _memoryTrace[index] : null;
            public int? length() => _memoryTrace.Count;
            // TODO: does it need to be an array? Why not ReadOnlySpan?
            public byte[]? slice(int start, int end) // needs looking into
            {
                if (start < 0 || end < 0 || start > _memoryTrace.Count || end > _memoryTrace.Count)
                {
                    return null;
                }

                List<byte> result = new List<byte>();
                for (int i = start; i < end; i++)
                {
                    result.Add(Convert.ToByte(_memoryTrace[i], 16));
                }

                return result.ToArray();
            }

            // TODO: does it need to be an array? Why not ReadOnlySpan?
            public byte[]? getUint(int offset) // needs looking into
            {
                if(offset < 0 || offset > _memoryTrace.Count)
                {
                    return null;
                }
                List<byte> result = new();
                for (int i = offset; i < offset + 32; i++)
                {
                    result.Add(Convert.ToByte(_memoryTrace[i], 16));
                }
                return result.ToArray();
            }
        }

        public class Contract
        {
            private readonly V8ScriptEngine _engine;
            private readonly Address _caller;
            private readonly Address _address;
            private readonly UInt256 _value;
            private readonly ReadOnlyMemory<byte> _input;

            public Contract(V8ScriptEngine engine, Address caller, Address address,UInt256 value, ReadOnlyMemory<byte> input)
            {
                _engine = engine;
                _address = address;
                _caller = caller;
                _value = value;
                _input = input;
            }

            public dynamic getAddress() => _engine.Script.Array.from(_address.Bytes);
            public dynamic getCaller() => _engine.Script.Array.from(_caller.Bytes);
            public dynamic getInput() => _engine.Script.Array.from(_input.ToArray());
            public UInt256 getValue() => _value;
        }
    }
}
