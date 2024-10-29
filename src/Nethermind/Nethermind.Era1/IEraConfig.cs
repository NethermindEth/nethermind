// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Era1;

public interface IEraConfig : IConfig
{
    [ConfigItem(Description = "Directory of era1 archives to be imported before starting in full sync mode.", DefaultValue = "", HiddenFromDocs = false)]
    public string? ImportDirectory { get; set; }

    [ConfigItem(Description = "Directory of archive export.", DefaultValue = "", HiddenFromDocs = false)]
    public string? ExportDirectory { get; set; }

    [ConfigItem(Description = "Start block to export", DefaultValue = "0", HiddenFromDocs = false)]
    long Start { get; set; }

    [ConfigItem(Description = "End block to export. Set to 0 for Head.", DefaultValue = "0", HiddenFromDocs = false)]
    long End { get; set; }

    [ConfigItem(Description = "Trusted accumulator file.", DefaultValue = "null", HiddenFromDocs = false)]
    string? TrustedAccumulatorFile { get; set; }

    [ConfigItem(Description = "Max era1 size.", DefaultValue = "8192", HiddenFromDocs = true)]
    int MaxEra1Size { get; set; }

    [ConfigItem(Description = "Network name used for era directory naming. When null, it will imply from block.", DefaultValue = "null", HiddenFromDocs = true)]
    string? NetworkName { get; set; }
}
