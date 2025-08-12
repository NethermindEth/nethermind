// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public class HistoryConfig : IHistoryConfig
{
    public bool Enabled => Pruning != PruningModes.Disabled;

    public PruningModes Pruning { get; set; } = PruningModes.Disabled;
    public long RetentionEpochs { get; set; } = 82125;
}
