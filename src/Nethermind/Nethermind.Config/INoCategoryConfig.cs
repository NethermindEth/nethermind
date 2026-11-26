// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Config;

[ConfigCategory(HiddenFromDocs = true)]
public interface INoCategoryConfig : IConfig
{
    [ConfigItem(Description = "Path to the configuration file.", IsSensitiveWhenMasked = true)]
    public string? Config { get; set; }

    [Obsolete("Has no effect.")]
    [ConfigItem(Description = "Deprecated. Has no effect.", DefaultValue = "null", IsSensitive = true)]
    public string? CliSwitchLocal { get; set; }
}
