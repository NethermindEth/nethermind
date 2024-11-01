// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

[ConfigCategory(HiddenFromDocs = true)]
public interface INoCategoryConfig : IConfig
{
    [ConfigItem(Description = "Path to the configuration file.")]
    public string Config { get; set; }

    [ConfigItem(Description = "Defines host value for CLI function \"switchLocal\".", DefaultValue = "http://localhost", EnvironmentVariable = "NETHERMIND_CLI_SWITCH_LOCAL")]
    public string CliSwitchLocal { get; set; }
}
