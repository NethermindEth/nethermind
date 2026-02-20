// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Test;

public class TestHardwareInfo(long availableMemory = 10000000, int? maxOpenFilesLimit = null) : IHardwareInfo
{
    public long AvailableMemoryBytes => availableMemory;
    public int? MaxOpenFilesLimit => maxOpenFilesLimit;
}
