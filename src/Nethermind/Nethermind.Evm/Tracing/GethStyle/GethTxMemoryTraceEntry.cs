// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethTxMemoryTraceEntry : GethTxTraceEntry
{
    internal override void UpdateMemorySize(ulong size)
    {
        base.UpdateMemorySize(size);

        // Geth's approach to memory trace is to show empty memory spaces on entry for the values that are being set by the operation
        Memory ??= Array.Empty<string>();

        int missingChunks = (int)((size - (ulong)Memory.Length * EvmPooledMemory.WordSize) / EvmPooledMemory.WordSize);

        if (missingChunks > 0)
        {
            var memory = Memory;
            Array.Resize(ref memory, memory.Length + missingChunks);
            for (int i = Memory.Length; i < memory.Length; i++)
            {
                memory[i] = "0000000000000000000000000000000000000000000000000000000000000000";
            }

            Memory = memory;
        }
    }
}
