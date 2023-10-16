// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Microsoft.ClearScript.V8;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethJavascriptStyleCtx
{
    public V8ScriptEngine Engine { get; set; } = null!;

    public string type { get; set; }
    public dynamic from { get; set; }
    public dynamic? to { get; set; }
    public dynamic input { get; set; }
    public BigInteger value { get; set; }
    public long gas { get; set; }
    public long gasUsed { get; set; }
    public ulong? gasPrice { get; set; }
    public long intrinsicGas { get; set; }
    public long block { get; set; }
    public dynamic output { get; set; }
    public string time { get; set; }
}
