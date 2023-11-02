// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class FrameResult
{
    private readonly V8ScriptEngine _engine;
    public FrameResult(V8ScriptEngine engine) => _engine = engine;
    public long GasUsed { get; set; }
    public byte[] Output { get; set; }
    public string? Error { get; set; }
    public long getGasUsed() => GasUsed;
    public ScriptObject getOutput() => Output.ToScriptArray(_engine);
    public string getError() => Error ?? string.Empty;
}
