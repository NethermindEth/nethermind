// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Int256;
using System.Linq;
using System.Numerics;


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
                if (rtn.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    rtn = rtn.Substring(2);
                }

                // Convert the hexadecimal string to a byte array
                byte[] byteArray = Enumerable.Range(0, rtn.Length / 2)
                    .Select(x => Convert.ToByte(rtn.Substring(x * 2, 2), 16))
                    .ToArray();
                BigInteger bigIntValue = new BigInteger(byteArray);
                return bigIntValue;
            }
            public string? getItem(int index) => index >= 0 && index < _items.Count ? _items[index] : null;
        }

        public class JSMemory
        {
            private readonly string _memoryTrace;
            public JSMemory(string memoryTrace)
            {
                _memoryTrace = memoryTrace;
            }

            public int? length() => _memoryTrace?.Length ?? 0;

            public byte[]? slice(int start, int end) // needs looking into
            {

                if (start < 0 || end < start || end > _memoryTrace.Length)
                {
                    throw new ArgumentOutOfRangeException("Invalid start or end values.");
                }

                int length = end - start;
                string memorySlice = _memoryTrace.Substring(start * 2, length * 2);

                byte[] byteArray = new byte[length];
                for (int i = 0; i < length * 2; i += 2)
                {
                    byteArray[i / 2] = Convert.ToByte(memorySlice.Substring(i, 2), 16);
                }

                return byteArray;
            }

            public byte[]? getUint(int offset) // needs looking into
            {
                if (offset < 0 || offset + 32 > _memoryTrace.Length)
                {
                    throw new ArgumentOutOfRangeException("Invalid offset.");
                }

                string uintHex = _memoryTrace.Substring(offset * 2, 64);

                byte[] byteArray = new byte[32];
                for (int i = 0; i < 64; i += 2)
                {
                    byteArray[i / 2] = Convert.ToByte(uintHex.Substring(i, 2), 16);
                }

                return byteArray;
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
                _caller = caller;
                _address = address;
                _value = value;
                _input = input;
            }

            public dynamic getAddress() => _engine.Script.Array.from(_address.Bytes);
            public dynamic getCaller() => _engine.Script.Array.from(_caller.Bytes);
            public dynamic getInput() => _engine.Script.Array.from(_input.ToArray());
            public UInt256 getValue() => _value;
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

            public CTX(V8ScriptEngine engine, string type, Address from, Address to, ReadOnlyMemory<byte> input, UInt256 value, long gas, UInt256 gasUsed, UInt256 gasPrice, UInt256 intrinsicGas, UInt256 block, ReadOnlyMemory<byte> output, string time)
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
            public dynamic from => _engine.Script.Array.from(_from.Bytes);
            public dynamic to => _engine.Script.Array.from(_to.Bytes);
            public dynamic input => _engine.Script.Array.from(_input.ToArray());
            public UInt256 value => _value;
            public long gas => _gas;
            public UInt256 gasUsed => _gasUsed;
            public UInt256 gasPrice => _gasPrice;
            public UInt256 intrinsicGas => _intrinsicGas;
            public UInt256 block => _block;
            public dynamic output => _engine.Script.Array.from(_output.ToArray());
            public string time => _time;
        }
    }
}
