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
    public Address From { get; set; }
    public Address To { get; set; }
    public ReadOnlyMemory<byte> Input { get; set; }
    public long Gas { get; set; }
    public UInt256 Value { get; set; }

    private ITypedArray<byte>? _from;
    private ITypedArray<byte>? _to;
    private ITypedArray<byte>? _input;
    private dynamic? _value;

    // ReSharper disable InconsistentNaming
    public string? getType() => Type;
    public ITypedArray<byte> getFrom() => _from ??= From.Bytes.ToTypedScriptArray();
    public ITypedArray<byte> getTo() => _to ??= To.Bytes.ToTypedScriptArray();
    public ITypedArray<byte> getInput() => _input ??= Input.ToTypedScriptArray();
    public long getGas() => Gas;
    public dynamic getValue() => _value ??= Value.ToBigInteger();
    // ReSharper restore InconsistentNaming
}
