// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.BlockAccessLists;

public sealed partial class BalReadStoragePlan
{
    private readonly Queue<BalReadCoverage> _workers = new();
}
