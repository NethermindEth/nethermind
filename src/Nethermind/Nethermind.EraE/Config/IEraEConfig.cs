// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.EraE.Config;
public interface IEraEConfig : IConfig
{
    [ConfigItem(Description = "Directory of erae archives to be imported.", DefaultValue = "", HiddenFromDocs = false)]
    string? ImportDirectory { get; set; }

    [ConfigItem(Description = "Directory for erae archive export.", DefaultValue = "", HiddenFromDocs = false)]
    string? ExportDirectory { get; set; }

    [ConfigItem(Description = "Block number to import/export from.", DefaultValue = "0", HiddenFromDocs = false)]
    long From { get; set; }

    [ConfigItem(Description = "Block number to import/export to. 0 means head.", DefaultValue = "0", HiddenFromDocs = false)]
    long To { get; set; }

    [ConfigItem(Description = "Accumulator file for trusting erae archives.", DefaultValue = "null", HiddenFromDocs = false)]
    string? TrustedAccumulatorFile { get; set; }

    [ConfigItem(Description = "Max number of blocks per era file.", DefaultValue = "8192", HiddenFromDocs = true)]
    int MaxEraSize { get; set; }

    [ConfigItem(Description = "Network name used for erae directory naming. When null, inferred from chain spec.", DefaultValue = "null", HiddenFromDocs = true)]
    string? NetworkName { get; set; }

    [ConfigItem(Description = "Maximum concurrency. 0 uses logical core count.", DefaultValue = "0", HiddenFromDocs = true)]
    int Concurrency { get; set; }

    [ConfigItem(Description = "[Technical] Block buffer size during era import.", DefaultValue = "4096", HiddenFromDocs = true)]
    long ImportBlocksBufferSize { get; set; }

    [ConfigItem(Description = "Beacon node URL for fetching beacon block roots and state roots during post-merge EraE export. When set, enables BeaconApiRootsProvider and HistoricalSummariesRpcProvider.", DefaultValue = "null", HiddenFromDocs = false)]
    string? BeaconNodeUrl { get; set; }
}
