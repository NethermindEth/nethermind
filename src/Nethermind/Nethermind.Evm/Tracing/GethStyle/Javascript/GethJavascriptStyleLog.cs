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
        public ulong? getPC() => (ulong?)pc;
        public ulong? getGas() => (ulong?)gas;
        public ulong? getCost() => (ulong?)gasCost;
        public int? getDepth() => depth;
        public ulong? getRefund() => (ulong?)refund;
        public dynamic? getError() => !string.IsNullOrEmpty(error) ? error : Undefined.Value;

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
            public List<string> Items { get; }
            public JSStack(List<string> items) => Items = items;
            public string? length() => Items.Count.ToString();
            public int? getCount() => Items.Count;
            public dynamic peek(int index) => new BigInteger(Bytes.FromHexString(Items[^(index + 1)]));
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

            public dynamic slice(BigInteger start, BigInteger end) // needs looking into
            {
                if (start < 0 || end < start || end > _memory.Count)
                {
                    throw new ArgumentOutOfRangeException("Invalid start or end values.");
                }

                int length = (int)(end - start);
                Span<byte> slice = CollectionsMarshal.AsSpan(_memory).Slice((int)start, length);
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
            public Address Caller { get; }
            private readonly Address? _address;
            private readonly ReadOnlyMemory<byte> _input;
            private object? _callerConverted;
            private object? _addressConverted;
            private object? _inputConverted;


            public Contract(V8ScriptEngine engine, Address caller, Address? address, UInt256 value, ReadOnlyMemory<byte> input)
            {
                _engine = engine;
                Caller = caller;
                _address = address;
                _value = value;
                _input = input;
            }

            public dynamic? getAddress() => _addressConverted ??= _address?.Bytes.ToScriptArray(_engine);
            public dynamic getCaller() => _callerConverted ??= Caller.Bytes.ToScriptArray(_engine);
            public dynamic getInput() => _inputConverted ??= _input.ToArray().ToScriptArray(_engine);
            public dynamic getValue() => (BigInteger)_value;
        }
    }
}
