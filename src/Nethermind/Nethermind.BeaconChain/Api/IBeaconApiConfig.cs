// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BeaconChain.Api;

/// <summary>
/// Configuration for the Beacon API REST/SSE host. Deliberately separate from
/// <see cref="IBeaconChainConfig"/>: the API is a self-contained surface with its own listener,
/// not a knob of the sync driver.
/// </summary>
public interface IBeaconApiConfig : IConfig
{
    [ConfigItem(Description = "Whether to serve the Beacon API (REST/SSE) surface.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The host the Beacon API Kestrel listener binds to.", DefaultValue = "\"127.0.0.1\"")]
    string Host { get; set; }

    [ConfigItem(Description = "The port the Beacon API listens on.", DefaultValue = "5052")]
    int Port { get; set; }
}
