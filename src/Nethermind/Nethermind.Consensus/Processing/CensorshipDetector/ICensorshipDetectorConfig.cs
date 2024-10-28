// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Consensus.Processing.CensorshipDetector;

public interface ICensorshipDetectorConfig : IConfig
{
    [ConfigItem(DefaultValue = "false", Description = "Whether to enable censorship detection.")]
    bool Enabled { get; set; }

    [ConfigItem(DefaultValue = "2",
        Description = "The number of the consecutive blocks with detected potential censorship to report.")]
    uint BlockCensorshipThreshold { get; set; }

    [ConfigItem(DefaultValue = "null", Description = "The addresses to detect censorship for.")]
    string[]? AddressesForCensorshipDetection { get; set; }
}
