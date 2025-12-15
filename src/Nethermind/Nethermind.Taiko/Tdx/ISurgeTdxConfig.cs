// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Taiko.Tdx;

public interface ISurgeTdxConfig : IConfig
{
    [ConfigItem(Description = "Path to the tdxs Unix socket.", DefaultValue = "/var/tdxs.sock")]
    string SocketPath { get; set; }

    [ConfigItem(Description = "Path to store TDX bootstrap data and keys.", DefaultValue = "~/.config/nethermind/tdx")]
    string ConfigPath { get; set; }

    [ConfigItem(Description = "On-chain registered instance ID. Set this after registering your TDX instance.", DefaultValue = "0")]
    uint InstanceId { get; set; }
}

