// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

public class HardwareInfo : IHardwareInfo
{
    public long AvailableMemoryBytes { get; }

    public HardwareInfo()
    {
        AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }
}
