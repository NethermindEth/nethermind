// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public record CallFrame
{
    public string? Type { get; set; }

    public Address From
    {
        get => _from;
        set
        {
            _from = value;
            _fromConverted = null;
        }
    }

    public Address To
    {
        get => _to;
        set
        {
            _to = value;
            _toConverted = null;
        }
    }

    public ReadOnlyMemory<byte> Input
    {
        get => _input;
        set
        {
            _input = value;
            _inputConverted = null;
        }
    }

    public long Gas { get; set; }

    public UInt256? Value
    {
        get => _value;
        set
        {
            _value = value;
            _valueConverted = null;
        }
    }

    private ITypedArray<byte>? _fromConverted;
    private ITypedArray<byte>? _toConverted;
    private ITypedArray<byte>? _inputConverted;
    private dynamic? _valueConverted;
    private Address _from;
    private Address _to;
    private ReadOnlyMemory<byte> _input;
    private UInt256? _value;

    // ReSharper disable InconsistentNaming
    public string? getType() => Type;
    public ITypedArray<byte> getFrom() => _fromConverted ??= From.Bytes.ToTypedScriptArray();
    public ITypedArray<byte> getTo() => _toConverted ??= To.Bytes.ToTypedScriptArray();
    public ITypedArray<byte> getInput() => _inputConverted ??= Input.ToTypedScriptArray();
    public long getGas() => Gas;
    public dynamic getValue() => (_valueConverted ??= Value?.ToBigInteger()) ?? Undefined.Value;
    // ReSharper restore InconsistentNaming
}
