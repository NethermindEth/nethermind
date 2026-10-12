// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs;

public partial class ChiadoSpecProvider
{
    public static readonly Address FeeCollector = new("0x1559000000000000000000000000000000000000");

    public static ChiadoSpecProvider Instance { get; } = new();
}
