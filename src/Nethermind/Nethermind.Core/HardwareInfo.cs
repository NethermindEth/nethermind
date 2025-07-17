// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

public class HardwareInfo : IHardwareInfo
{
    public long AvailableMemoryBytes { get; }

    public HardwareInfo()
    {
        // Note: Not the same as memory capacity. This take into account current system memory pressure such as
        // other process as well as OS level limit such as rlimit. Eh, its good enough.
        AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }
}
