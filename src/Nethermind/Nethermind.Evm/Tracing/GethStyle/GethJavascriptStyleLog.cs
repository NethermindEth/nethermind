// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Microsoft.ClearScript.V8;
using Nethermind.Int256;


namespace Nethermind.Evm.Tracing.GethStyle
{

    public class GethJavascriptStyleLog : GethTxTraceEntry
    {
        private static V8ScriptEngine _engine = new V8ScriptEngine();
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
        public long? getPC()
        {
            return pc;
        }
        public long? getGas()
        {
            return gas;
        }
        public long? getCost()
        {
            return gasCost;
        }
        public int? getDepth()
        {
            return depth;
        }
        public long? getRefund()
        {
            return refund;
        }

        public string? getError() //needs looking into
        {
            return !string.IsNullOrEmpty(error) ? error : null;
        }

        public class OpcodeString
        {
            private readonly Instruction _value;

            public OpcodeString(Instruction value)
            {
                _value = value;
            }

            public string? toNumber() => _value.GetHex();

            public string? toString() => _value.GetName();

            public bool? isPush()
            {
                return _value >= Instruction.PUSH0 && _value <= Instruction.PUSH32;

            }

        }

        public class JSStack
        {
            private readonly List<string> _items;

            public JSStack(List<string> items)
            {
                _items = items;
            }

            public string? length() => _items.Count.ToString();

            public int? getCount() => _items.Count;

            public string? peek(int index)
            {
                int topIndex = _items.Count - 1 - index;
                if (topIndex >= 0 && topIndex < _items.Count)
                {
                    return _items[topIndex];
                }
                return null;
            }
            public string? getItem(int index)
            {
                if (index >= 0 && index < _items.Count)
                {
                    return _items[index];
                }
                return null;
            }

        }

        public class JSMemory
        {
            private readonly List<string> _memoryTrace;

            public JSMemory(List<string> memoryTrace)
            {
                _memoryTrace = memoryTrace;
            }
            public int? getCount() => _memoryTrace.Count;

            public string? getItem(int index)
            {
                if (index >= 0 && index < _memoryTrace.Count)
                {
                    return _memoryTrace[index];
                }
                return null;
            }
            public int? length() => _memoryTrace.Count;

            public byte[]? Slice(int start, int end) // needs looking into
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
            // private readonly ScriptEngine _engine;
            private readonly Address _caller;
            private readonly Address _address;
            private readonly UInt256 _value;
            private readonly ReadOnlyMemory<byte> _input;

            public Contract( Address caller, Address address,UInt256 value, ReadOnlyMemory<byte> input)
            {
                _address = address;
                _caller = caller;
                _value = value;
                _input = input;
            }

            public dynamic getAddress()
            {
                dynamic byteAdrress = _engine.Script.Array.from(_address.Bytes);
                return byteAdrress;
            }

            public dynamic getCaller()
            {
                dynamic byteCaller = _engine.Script.Array.from(_caller.Bytes);
                return byteCaller;
            }

            public dynamic getInput()
            {
                var dataBytes = _input.ToArray();

                dynamic dataArray = _engine.Script.Array.from(dataBytes);
                return dataArray;
            }

            public UInt256 getValue()
            {
                return _value;
            }
        }
    }
}
