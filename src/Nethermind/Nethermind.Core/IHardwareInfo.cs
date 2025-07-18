// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Core;

public interface IHardwareInfo
{
    public static readonly long StateDbLargerMemoryThreshold = 32.GiB();
    long AvailableMemoryBytes { get; }
}
