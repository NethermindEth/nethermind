// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Taiko.Config;

public interface ITaikoConfig : IConfig
{
    [ConfigItem(Description = "The URL of the Taiko L1 execution node JSON-RPC API.", DefaultValue = "http://host.docker.internal:32002")]
    string? L1EthApiEndpoint { get; set; }
}
