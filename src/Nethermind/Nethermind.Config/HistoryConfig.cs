// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public class HistoryConfig : IHistoryConfig
{
    public bool Enabled => HistoryRetentionEpochs is not null || DropPreMerge;

    public ulong? HistoryRetentionEpochs { get; set; } = null;
    public bool DropPreMerge { get; set; } = false;
}
