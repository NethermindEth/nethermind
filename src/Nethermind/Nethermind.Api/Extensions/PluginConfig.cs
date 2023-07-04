// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Api.Extensions;

public class PluginConfig : IPluginConfig
{
    public string[] PluginOrder { get; set; } = { "Clique", "Aura", "Ethash", "AuRaMerge", "Merge", "MEV", "HealthChecks", "Hive" };
}
