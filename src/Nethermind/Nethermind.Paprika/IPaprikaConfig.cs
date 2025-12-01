// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Paprika;

public interface IPaprikaConfig: IConfig
{
    [ConfigItem(Description = "Enable paprika", DefaultValue = "false", HiddenFromDocs = true)]
    public bool Enabled { get; set; }

    [ConfigItem(Description = "Paprika db size in GB", DefaultValue = "128", HiddenFromDocs = true)]
    public long SizeGb { get; set; }

    [ConfigItem(Description = "Paprika history depth", DefaultValue = "128", HiddenFromDocs = true)]
    public byte HistoryDepth { get; set; }

    [ConfigItem(Description = "Paprika flush", DefaultValue = "false", HiddenFromDocs = true)]
    public bool FlushToDisk { get; set; }

    [ConfigItem(Description = "Paprika flush", DefaultValue = "2", HiddenFromDocs = true)]
    int FinalizationQueueLimit { get; set; }

    [ConfigItem(Description = "Automatic finalaization", DefaultValue = "2", HiddenFromDocs = true)]
    int AutomaticallyFinalizeAfter { get; set; }

    [ConfigItem(Description = "Import from triestore", DefaultValue = "false", HiddenFromDocs = true)]
    bool ImportFromTrieStore { get; set; }
}
