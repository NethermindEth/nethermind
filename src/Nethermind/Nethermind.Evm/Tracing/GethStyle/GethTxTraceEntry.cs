// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethTxTraceEntry
{
    public int Depth { get; set; }

    public string? Error { get; set; }

    public long Gas { get; set; }

    public long GasCost { get; set; }

    public IList<string>? Memory { get; set; }

    public string? Opcode { get; set; }

    public long ProgramCounter { get; set; }

    public IList<string>? Stack { get; set; }

    public Dictionary<string, string>? Storage { get; set; }

    internal virtual void UpdateMemorySize(ulong size) { }
}
