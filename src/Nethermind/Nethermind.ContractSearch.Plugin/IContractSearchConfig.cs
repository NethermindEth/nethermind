// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.ContractSearch.Plugin;

public interface IContractSearchConfig : IConfig
{
    [ConfigItem(
    Description = "Activates or Deactivates Search Plugin",
    DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(
        Description = "Sets the file to which the search results are dumped",
        DefaultValue = "null")]
    string? File { get; set; }
}
