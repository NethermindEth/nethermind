// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethTxMemoryTraceEntry : GethTxTraceEntry
{
    internal override void UpdateMemorySize(ulong size)
    {
        base.UpdateMemorySize(size);

        // Geth's approach to memory trace is to show empty memory spaces on entry for the values that are being set by the operation
        Memory ??= new List<string>();

        int missingChunks = (int)((size - (ulong)Memory.Count * EvmPooledMemory.WordSize) / EvmPooledMemory.WordSize);

        for (int i = 0; i < missingChunks; i++)
        {
            Memory.Add("0000000000000000000000000000000000000000000000000000000000000000");
        }
    }
}
