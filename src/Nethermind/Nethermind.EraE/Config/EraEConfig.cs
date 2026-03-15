// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EraE.Archive;

namespace Nethermind.EraE.Config;

public class EraEConfig : IEraEConfig
{
    public string? ImportDirectory { get; set; }
    public string? ExportDirectory { get; set; }
    public long From { get; set; }
    public long To { get; set; }
    public string? TrustedAccumulatorFile { get; set; }
    public int MaxEraSize { get; set; } = EraWriter.MaxEraSize;
    public string? NetworkName { get; set; }
    public int Concurrency { get; set; }
    public long ImportBlocksBufferSize { get; set; } = 4096;
    public string? BeaconNodeUrl { get; set; }
    public string? RemoteBaseUrl { get; set; }
    public string? RemoteDownloadDirectory { get; set; }
    public string RemoteChecksumFile { get; set; } = "checksums_sha256.txt";
}
