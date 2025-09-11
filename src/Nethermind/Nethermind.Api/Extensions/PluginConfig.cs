// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Api.Extensions;

public class PluginConfig : IPluginConfig
{
    public string[] PluginOrder { get; set; } = { "HealthChecks", "Clique", "Aura", "Ethash", "Optimism", "Shutter", "Taiko", "AuRaMerge", "Merge", "Flashbots", "MEV", "Hive" };
}
