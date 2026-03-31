// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

public class StateCompositionConfig : IStateCompositionConfig
{
    public bool Enabled { get; set; } = true;
    public int ScanQueueTimeoutSeconds { get; set; } = 5;
    public int ScanParallelism { get; set; } = 4;
    public long ScanMemoryBudget { get; set; } = 1_000_000_000;
    public int TopNContracts { get; set; } = 20;
    public bool ExcludeStorage { get; set; }
    public int ScanCooldownSeconds { get; set; } = 60;
}
