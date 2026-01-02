// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Era1;

public class EraConfig : IEraConfig
{
    public string? ImportDirectory { get; set; }
    public string? ExportDirectory { get; set; }
    public long From { get; set; }
    public long To { get; set; }
    public string? TrustedAccumulatorFile { get; set; }
    public int MaxEra1Size { get; set; } = EraWriter.MaxEra1Size;
    public string? NetworkName { get; set; }
    public int Concurrency { get; set; }
    public long ImportBlocksBufferSize { get; set; } = 1024 * 4;
}
