// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Optimism;

public interface IOptimismConfig : IConfig
{
    [ConfigItem(Description = "The sequencer address.", DefaultValue = "null")]
    string? SequencerUrl { get; set; }
    [ConfigItem(Description = "Use enshrined op cl.", DefaultValue = "false")]
    bool ClEnabled { get; set; }
    [ConfigItem(Description = "CL p2p communication host", DefaultValue = "null")]
    public string? ClP2PHost { get; set; }
    [ConfigItem(Description = "CL p2p communication host", DefaultValue = "3030")]
    public int ClP2PPort { get; set; }
    [ConfigItem(Description = "URL to L1 beacon node", DefaultValue = "null")]
    string? L1BeaconApiEndpoint { get; set; }
    [ConfigItem(Description = "URL to L1 execution node.", DefaultValue = "null")]
    string? L1EthApiEndpoint { get; set; }
}
