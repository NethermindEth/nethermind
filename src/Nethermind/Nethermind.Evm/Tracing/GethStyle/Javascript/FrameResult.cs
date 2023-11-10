// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class FrameResult
{
    private ITypedArray<byte>? output;
    public long GasUsed { get; set; }
    public byte[] Output { get; set; }
    public string? Error { get; set; }
    public long getGasUsed() => GasUsed;
    public ITypedArray<byte> getOutput() => output ??= Output.ToTypedScriptArray();
    public dynamic getError() => !string.IsNullOrEmpty(Error) ? Error : Undefined.Value;
}
