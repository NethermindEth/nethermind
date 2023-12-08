// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace Nethermind.Evm.Tracing.GethStyle.JavaScript;

public class FrameResult
{
    private ITypedArray<byte>? _outputConverted;
    private byte[] _output;
    public long GasUsed { get; set; }

    public byte[] Output
    {
        get => _output;
        set
        {
            _output = value;
            _outputConverted = null;
        }
    }

    public string? Error { get; set; }
    public long getGasUsed() => GasUsed;
    public ITypedArray<byte> getOutput() => _outputConverted ??= Output.ToTypedScriptArray();
    public dynamic getError() => !string.IsNullOrEmpty(Error) ? Error : Undefined.Value;
}
