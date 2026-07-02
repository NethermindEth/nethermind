// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.History;

public class HistoryConfig : IHistoryConfig
{
    public PruningModes Pruning { get; set; } = PruningModes.Disabled;
    public uint RetentionEpochs { get; set; } = 82125;
    public uint BalRetentionEpochs { get; set; } = 3533;
    public ulong PruningInterval { get; set; } = 8;
    public uint PruningTimeoutSeconds { get; set; } = 2;
    public bool AllowBelowMinRetention { get; set; } = false;
}
