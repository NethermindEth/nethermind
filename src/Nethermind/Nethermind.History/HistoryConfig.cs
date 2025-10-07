// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.History;

public class HistoryConfig : IHistoryConfig
{
    public PruningModes Pruning { get; set; } = PruningModes.Disabled;
    public uint RetentionEpochs { get; set; } = 82125;
    public uint PruningInterval { get; set; } = 8;
}
