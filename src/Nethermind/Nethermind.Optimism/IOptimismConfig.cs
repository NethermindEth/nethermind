// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Optimism;

public interface IOptimismConfig : IConfig
{
    [ConfigItem(Description = "Sequencer address", DefaultValue = "null")]
    string? SequencerUrl { get; set; }
}
