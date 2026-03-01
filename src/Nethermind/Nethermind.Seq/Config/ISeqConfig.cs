// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Seq.Config;

[ConfigCategory(Description = "Configuration of the Seq logging server integration.")]
public interface ISeqConfig : IConfig
{
    [ConfigItem(Description = "The min log level to sent to Seq.", DefaultValue = "Off")]
    string MinLevel { get; }

    [ConfigItem(Description = "The Seq instance URL.", DefaultValue = "http://localhost:5341")]
    string ServerUrl { get; }

    [ConfigItem(Description = "The Seq API key.", DefaultValue = "")]
    string ApiKey { get; }
}
