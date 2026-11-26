// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

namespace Nethermind.Core.BlockAccessLists;

public sealed partial class BalReadStoragePlan
{
    private readonly ConcurrentQueue<BalReadCoverage> _workers = new();
}
