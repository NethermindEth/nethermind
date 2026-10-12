// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs;

// Lazy, so the guest runs no class constructor for this type.
public partial class ChiadoSpecProvider
{
    public static Address FeeCollector => field ??= new("0x1559000000000000000000000000000000000000");

    public static ChiadoSpecProvider Instance => field ??= new();
}
