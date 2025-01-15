// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;

namespace Nethermind.Optimism.CL;

public interface ICLConfig : IConfig
{
    [ConfigItem(Description = "Use enshrined op cl.", DefaultValue = "false")]
    bool Enabled { get; set; }
    [ConfigItem(Description = "CL p2p communication host", DefaultValue = "127.0.0.1")]
    public string P2PHost { get; set; }
    [ConfigItem(Description = "CL p2p communication host", DefaultValue = "3030")]
    public int P2PPort { get; set; }
    [ConfigItem(Description = "URL to L1 beacon node", DefaultValue = "null")]
    string? L1BeaconApiEndpoint { get; set; }
    [ConfigItem(Description = "URL to L1 execution node.", DefaultValue = "null")]
    string? L1EthApiEndpoint { get; set; }
}
