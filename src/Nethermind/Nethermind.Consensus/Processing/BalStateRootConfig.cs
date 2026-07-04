// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing;

public class BalStateRootConfig : IBalStateRootConfig
{
    public bool Enabled { get; set; } = false;
    public bool UseGpu { get; set; } = false;
    public int GpuMinBatch { get; set; } = 4096;
}
