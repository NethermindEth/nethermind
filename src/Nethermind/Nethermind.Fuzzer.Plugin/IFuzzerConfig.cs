// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Fuzzer.Plugin;

[ConfigCategory(Description = "Controls the in-process log fuzzer used for crash reproduction.")]
public interface IFuzzerConfig : IConfig
{
    [ConfigItem(
        Description = "Enables the in-process log fuzzer plugin.",
        DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(
        Description = """
            Phrases and required counts that define readiness before fuzzing begins.
            Format: Phrase:Count;Other Phrase:Count. Counts default to 20 when omitted.
            """,
        DefaultValue = "Processed:20;Synced Chain Head:20")]
    string ThresholdPhrases { get; set; }

    [ConfigItem(
        Description = "Destination file where the triggering log entry is stored before the process terminates.",
        DefaultValue = "fuzzer-trigger.log")]
    string? TriggerFilePath { get; set; }
}
