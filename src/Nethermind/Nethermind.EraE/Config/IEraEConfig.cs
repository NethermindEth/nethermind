// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.EraE.Config;

public interface IEraEConfig : IConfig
{
    [ConfigItem(Description = "Directory of EraE archives to be imported.", DefaultValue = "")]
    string? ImportDirectory { get; set; }

    [ConfigItem(Description = "Directory for EraE archive export.", DefaultValue = "")]
    string? ExportDirectory { get; set; }

    [ConfigItem(Description = "Block number to import/export from.", DefaultValue = "0")]
    long From { get; set; }

    [ConfigItem(Description = "Block number to import/export to. 0 means head.", DefaultValue = "0")]
    long To { get; set; }

    [ConfigItem(Description = "Accumulator file for trusting EraE archives.", DefaultValue = "null")]
    string? TrustedAccumulatorFile { get; set; }

    [ConfigItem(Description = "Max number of blocks per era file.", DefaultValue = "8192", HiddenFromDocs = true)]
    int MaxEraSize { get; set; }

    [ConfigItem(Description = "Network name used for EraE directory naming. When null, inferred from chain spec.", DefaultValue = "null", HiddenFromDocs = true)]
    string? NetworkName { get; set; }

    [ConfigItem(Description = "Export parallelism. 0 = ProcessorCount/4 (default, background-friendly). -1 = all cores. >0 = exact.", DefaultValue = "0", HiddenFromDocs = true)]
    int Concurrency { get; set; }

    [ConfigItem(Description = "[Technical] Block buffer size during era import.", DefaultValue = "4096", HiddenFromDocs = true)]
    long ImportBlocksBufferSize { get; set; }

    [ConfigItem(Description = "Beacon node URL for fetching beacon block roots and state roots during post-merge EraE export. When set, enables BeaconApiRootsProvider and HistoricalSummariesRpcProvider.", DefaultValue = "null")]
    string? BeaconNodeUrl { get; set; }

    [ConfigItem(Description = "Base URL of a remote EraE archive server (e.g. https://data.ethpandaops.io/erae/{network}/). When set, missing local epoch files are downloaded on demand.", DefaultValue = "null")]
    string? RemoteBaseUrl { get; set; }

    [ConfigItem(Description = "Local directory where remotely downloaded EraE files are cached. Defaults to ImportDirectory when null.", DefaultValue = "null")]
    string? RemoteDownloadDirectory { get; set; }

    [ConfigItem(Description = "Filename of the checksum manifest on the remote server.", DefaultValue = "checksums_sha256.txt", HiddenFromDocs = true)]
    string RemoteChecksumFile { get; set; }
}
