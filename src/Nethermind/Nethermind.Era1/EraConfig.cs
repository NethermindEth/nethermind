// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

public class EraConfig : IEraConfig
{
    public string? ImportDirectory { get; set; }
    public string? ExportDirectory { get; set; }
    public ulong From { get; set; }
    public ulong To { get; set; }
    public string? TrustedAccumulatorFile { get; set; }
    public ulong MaxEra1Size { get; set; } = EraWriter.MaxEra1Size;
    public string? NetworkName { get; set; }
    public int Concurrency { get; set; }
    public ulong ImportBlocksBufferSize { get; set; } = 1024 * 4;
}
