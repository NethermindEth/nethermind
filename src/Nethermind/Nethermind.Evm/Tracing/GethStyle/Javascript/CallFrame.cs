// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Microsoft.ClearScript;
using Nethermind.Core;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public record CallFrame
{
    public string? Type { get; set; }
    public Address From { get; set; }
    public Address To { get; set; }
    public byte[] Input { get; set; }
    public long Gas { get; set; }
    public BigInteger Value { get; set; }

    private ScriptObject? _from = null;
    private ScriptObject? _to = null;
    private ScriptObject? _input = null;

    // ReSharper disable InconsistentNaming
    public string? getType() => Type;
    public ScriptObject getFrom() => _from ??= From.Bytes.ToScriptArray();
    public ScriptObject getTo() => _to ??= To.Bytes.ToScriptArray();
    public ScriptObject getInput() => _input ??= Input.ToScriptArray();
    public long getGas() => Gas;
    public BigInteger getValue() => Value;
    // ReSharper restore InconsistentNaming
}
