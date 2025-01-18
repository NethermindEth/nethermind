// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public class HistoryConfig : IHistoryConfig
{
    public ulong? HistoryPruneEpochs { get; set; }
    public bool DropPreMerge { get; set; }
    public int PruningTimeout { get; set; } = 500;
}
