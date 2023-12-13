// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Api.Extensions;

public interface IPluginConfig : IConfig
{
    [ConfigItem(Description = "Order of plugin initialization", DefaultValue = "[Clique, Aura, Ethash, Optimism, AuRaMerge, Merge, MEV, HealthChecks, Hive]")]
    string[] PluginOrder { get; set; }
}
