// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Microsoft.ClearScript;
using Nethermind.Core;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public record CallFrame
{
    private readonly ScriptEngine _engine;

    public CallFrame(ScriptEngine engine)
    {
        _engine = engine;
    }

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
    public ScriptObject getFrom() => _from ??= From.Bytes.ToScriptArray(_engine);
    public ScriptObject getTo() => _to ??= To.Bytes.ToScriptArray(_engine);
    public ScriptObject getInput() => _input ??= Input.ToScriptArray(_engine);
    public long getGas() => Gas;
    public BigInteger getValue() => Value;
    // ReSharper restore InconsistentNaming
}
