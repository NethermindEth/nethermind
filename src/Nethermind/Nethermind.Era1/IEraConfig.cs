// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Era1;

public interface IEraConfig : IConfig
{
    [ConfigItem(Description = "Directory of era1 archives to be imported before starting in full sync mode.", DefaultValue = "", HiddenFromDocs = false)]
    public string? ImportDirectory { get; set; }
}
