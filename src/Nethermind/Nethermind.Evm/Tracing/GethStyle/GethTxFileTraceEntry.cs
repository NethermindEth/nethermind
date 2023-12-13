// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethTxFileTraceEntry : GethTxTraceEntry
{
    public ulong? MemorySize { get; set; }

    public Instruction? OpcodeRaw { get; set; }

    public long? Refund { get; set; }

    internal override void UpdateMemorySize(ulong size)
    {
        base.UpdateMemorySize(size);

        MemorySize = size;
    }
}
