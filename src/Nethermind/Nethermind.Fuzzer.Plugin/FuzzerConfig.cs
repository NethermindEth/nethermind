// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Fuzzer.Plugin;

public class FuzzerConfig : IFuzzerConfig
{
    public bool Enabled { get; set; }
    public string ThresholdPhrases { get; set; } = "Processed:20;Synced Chain Head:20";
    public string? TriggerFilePath { get; set; } = "fuzzer-trigger.log";
}
