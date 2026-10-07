// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public partial class BlocksConfig
{
    public double SingleBlockImprovementOfSlot { get; set; } = 0.25;
}
