// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Era1;

public interface IEraConfig : IConfig
{
    [ConfigItem(Description = "Directory of era1 archives to be imported.", DefaultValue = "", HiddenFromDocs = false)]
    string? ImportDirectory { get; set; }

    [ConfigItem(Description = "Directory of archive export.", DefaultValue = "", HiddenFromDocs = false)]
    string? ExportDirectory { get; set; }

    [ConfigItem(Description = "Block number to import/export from.", DefaultValue = "0", HiddenFromDocs = false)]
    long From { get; set; }

    [ConfigItem(Description = "Block number to import/export to.", DefaultValue = "0", HiddenFromDocs = false)]
    long To { get; set; }

    [ConfigItem(Description = "Accumulator file to be used for trusting era files.", DefaultValue = "null", HiddenFromDocs = false)]
    string? TrustedAccumulatorFile { get; set; }

    [ConfigItem(Description = "Max era1 size.", DefaultValue = "8192", HiddenFromDocs = true)]
    int MaxEra1Size { get; set; }

    [ConfigItem(Description = "Network name used for era directory naming. When null, it will imply from chain spec.", DefaultValue = "null", HiddenFromDocs = true)]
    string? NetworkName { get; set; }

    [ConfigItem(Description = "Maximum concurrency. Set to 0 to use logical core count. Lowering this may improve performance in some configuration.", DefaultValue = "0", HiddenFromDocs = true)]
    int Concurrency { get; set; }

    [ConfigItem(Description = "[Technical] Buffer size during full sync when era importing. Lower number reduces memory usage.", DefaultValue = "4096", HiddenFromDocs = true)]
    long ImportBlocksBufferSize { get; set; }
}
