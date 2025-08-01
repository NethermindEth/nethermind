// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public class HistoryConfig : IHistoryConfig
{
    public bool Enabled => RetentionEpochs is not null || DropPreMerge;

    public long? RetentionEpochs { get; set; } = null;
    public bool DropPreMerge { get; set; } = false;
}
