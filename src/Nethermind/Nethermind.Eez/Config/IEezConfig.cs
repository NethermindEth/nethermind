// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Eez.Config;

[ConfigCategory(Description = "EEZ rollup L2 execution rules")]
public interface IEezConfig : IConfig
{
    [ConfigItem(Description = "Whether to execute the chain under the EEZ L2 rules. Requires an EEZ genesis.", DefaultValue = "false")]
    bool Enabled { get; set; }
}
