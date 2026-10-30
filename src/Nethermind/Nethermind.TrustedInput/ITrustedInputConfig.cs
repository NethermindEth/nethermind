// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.TrustedInput;

public interface ITrustedInputConfig : IConfig
{
    [ConfigItem(Description = "Whether to capture a trusted zkVM guest input for every block the node processes, and serve it over `trustedinput_getInput`.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The number of most recent captured inputs kept in memory.", DefaultValue = "64")]
    int CacheSize { get; set; }

    [ConfigItem(Description = "The directory each captured input is also written to, as `<number>-<hash>.nti`. No files are written when unset.", DefaultValue = "null")]
    string? OutputDirectory { get; set; }
}
