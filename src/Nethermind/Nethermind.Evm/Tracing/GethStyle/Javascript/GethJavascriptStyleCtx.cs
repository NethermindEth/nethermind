// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.ClearScript.V8;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethJavascriptStyleCtx
{
    private readonly V8ScriptEngine _engine;

   public GethJavascriptStyleCtx(V8ScriptEngine engine) => _engine = engine;

    public dynamic type { get; set; }
    public dynamic from { get; set; }
    public dynamic to { get; set; }
    public dynamic input { get; set; }
    public dynamic value { get; set; }
    public long gas { get; set; }
    public dynamic gasUsed { get; set; }
    public dynamic gasPrice { get; set; }
    public dynamic intrinsicGas { get; set; }
    public dynamic block { get; set; }
    public dynamic output { get; set; }
    public string time { get; set; }
}
