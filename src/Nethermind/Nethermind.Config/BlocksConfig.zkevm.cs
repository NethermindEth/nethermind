// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public partial class BlocksConfig
{
    // The 0.25 default emits an FP constant load the guest's ISA gate rejects; only block production reads it.
    public double SingleBlockImprovementOfSlot { get; set; }
}
