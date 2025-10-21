// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Optimism;

public interface IOptimismConfig : IConfig
{
    [ConfigItem(Description = "The Optimism sequencer URL.", DefaultValue = "null")]
    string? SequencerUrl { get; set; }

    [ConfigItem(Description = "Whether to use the enshrined Optimism consensus layer.", DefaultValue = "false", HiddenFromDocs = true)]
    bool ClEnabled { get; set; }

    [ConfigItem(Description = "The Optimism consensus layer host.", DefaultValue = "null", HiddenFromDocs = true)]
    public string? ClP2PHost { get; set; }

    [ConfigItem(Description = "CL p2p communication host", DefaultValue = "3030", HiddenFromDocs = true)]
    public int ClP2PPort { get; set; }

    [ConfigItem(Description = "The URL of the Optimism L1 consensus node API.", DefaultValue = "null", HiddenFromDocs = true)]
    string? L1BeaconApiEndpoint { get; set; }

    [ConfigItem(Description = "The URL of the Optimism L1 execution node JSON-RPC API.", DefaultValue = "null", HiddenFromDocs = true)]
    string? L1EthApiEndpoint { get; set; }
}
