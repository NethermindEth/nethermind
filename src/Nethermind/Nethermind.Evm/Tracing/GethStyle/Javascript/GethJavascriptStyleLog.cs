// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Int256;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.ClearScript;



// ReSharper disable InconsistentNaming

namespace Nethermind.Evm.Tracing.GethStyle.Javascript
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

        public CTX? ctx { get; set; }
        public long? getPC() => pc;

        public long? getGas() => gas;

        public long? getCost() => gasCost;

        public int? getDepth() => depth;

        public long? getRefund() => refund;

        public dynamic? getError() //needs looking into
            => !string.IsNullOrEmpty(error) ? error : Undefined.Value;

        public class OpcodeString
        {

            private readonly Instruction _value;

            public OpcodeString(Instruction value) => _value = value;

            public dynamic? toNumber() => _value.GetHex();
            public string? toString() => _value.GetName();
            public bool? isPush() => _value is >= Instruction.PUSH0 and <= Instruction.PUSH32;
        }

        public class JSStack
        {
            private readonly List<string> _items;
            private readonly V8ScriptEngine _engine;

            public JSStack(V8ScriptEngine engine, List<string> items)
            {
                _engine = engine;
                _items = items;
            }

            public string? length() => _items.Count.ToString();

            public int? getCount() => _items.Count;

            public dynamic? peek(int index) //needs looking into
            {
                int topIndex = _items.Count - 1 - index;
                string rtn = topIndex >= 0 && topIndex < _items.Count ? _items[topIndex] : null;
                rtn = rtn?.Substring(2);
                // Convert the hexadecimal string to a byte array
                byte[] byteArray = Enumerable.Range(0, rtn.Length / 2)
                    .Select(x => Convert.ToByte(rtn.Substring(x * 2, 2), 16))
                    .ToArray();
                BigInteger bigIntValue = new (byteArray);
                return bigIntValue;
            }

            public string? getItem(int index) => index >= 0 && index < _items.Count ? _items[index] : null;
        }

        public class JSMemory
        {
            private readonly V8ScriptEngine _engine;
            private readonly List<byte> _memory;

            public JSMemory(V8ScriptEngine engine, List<byte> memory)
            {
                _engine = engine;
                _memory = memory;
            }

            public int? length() => _memory.Count; // / EvmPooledMemory.WordSize?

            public dynamic slice(int start, int end) // needs looking into
            {
                if (start < 0 || end < start || end > _memory.Count)
                {
                    throw new ArgumentOutOfRangeException("Invalid start or end values.");
                }

                int length = end - start;
                Span<byte> slice = CollectionsMarshal.AsSpan(_memory).Slice(start, length);
                return slice.ToArray().ToScriptArray(_engine);
            }

            public dynamic? getUint(int offset)
            {
                if (offset < 0 || offset + 32 > _memory.Count)
                {
                    throw new ArgumentOutOfRangeException("Invalid offset.");
                }

                Span<byte> byteArray = CollectionsMarshal.AsSpan(_memory).Slice(offset, 32); // Adjust the length to 32 bytes
                return byteArray.ToArray().ToScriptArray(_engine);
            }

        }

        public class Contract
        {
            private readonly V8ScriptEngine _engine;
            private readonly UInt256 _value;
            private readonly Address _caller;
            private readonly Address _address;
            private readonly ReadOnlyMemory<byte> _input;
            private object? _callerConverted;
            private object? _addressConverted;
            private object? _inputConverted;


            public Contract(V8ScriptEngine engine, Address caller, Address address, UInt256 value, ReadOnlyMemory<byte> input)
            {
                _engine = engine;
                _caller = caller;
                _address = address;
                _value = value;
                _input = input;
            }

            public dynamic getAddress() => _addressConverted ??= _address.Bytes.ToScriptArray(_engine);
            public dynamic getCaller() => _callerConverted ??= _caller.Bytes.ToScriptArray(_engine);
            public dynamic getInput() => _inputConverted ??= _input.ToArray().ToScriptArray(_engine);
            public dynamic getValue() => _value.ToInt64(null);
        }

        public class CTX
        {
            private readonly V8ScriptEngine _engine;
            private readonly string _type;
            private readonly Address _from;
            private readonly Address _to;
            private readonly ReadOnlyMemory<byte> _input;
            private readonly UInt256 _value;
            private readonly long _gas;
            private readonly UInt256 _gasUsed;
            private readonly UInt256 _gasPrice;
            private readonly UInt256 _intrinsicGas;
            private readonly UInt256 _block;
            private readonly ReadOnlyMemory<byte> _output;
            private readonly string _time;

            private object? _fromConverted;
            private object? _toConverted;
            private object? _inputConverted;
            private object? _outputConverted;


            public CTX(V8ScriptEngine engine, string type,  Address from,Address to, ReadOnlyMemory<byte> input, UInt256 value, long gas, UInt256 gasUsed, UInt256 gasPrice, UInt256 intrinsicGas, UInt256 block, ReadOnlyMemory<byte> output,
                string time)
            {
                _engine = engine;
                _type = type;
                _from = from;
                _to = to;
                _input = input;
                _value = value;
                _gas = gas;
                _gasUsed = gasUsed;
                _gasPrice = gasPrice;
                _intrinsicGas = intrinsicGas;
                _block = block;
                _output = output;
                _time = time;
            }

            public string type => _type;
            public dynamic from => _fromConverted ??= _from.Bytes.ToScriptArray(_engine);
            public dynamic to => _toConverted ??= _to.Bytes.ToScriptArray(_engine);
            public dynamic input => _inputConverted ??= _input.ToArray().ToScriptArray(_engine);
            public dynamic value => _value.ToInt64(null);
            public long gas => _gas;
            public dynamic gasUsed => _gasUsed.ToInt64(null);
            public dynamic gasPrice => _gasPrice.ToInt64(null);
            public dynamic intrinsicGas => _intrinsicGas.ToInt64(null);
            public dynamic block => _block.ToInt64(null);
            public dynamic output => _outputConverted ??= _output.ToArray().ToScriptArray(_engine);
            public string time => _time;
        }


    }
}
