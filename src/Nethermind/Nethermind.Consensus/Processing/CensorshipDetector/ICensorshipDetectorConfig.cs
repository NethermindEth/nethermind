// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Consensus.Processing.CensorshipDetector;

public interface ICensorshipDetectorConfig : IConfig
{
    [ConfigItem(DefaultValue = "null",
        Description = "The addresses for which censorship is being detected.")]
    string[]? AddressesForCensorshipDetection { get; set; }
}
