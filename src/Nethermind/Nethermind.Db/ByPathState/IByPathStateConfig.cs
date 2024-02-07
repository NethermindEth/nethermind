// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db.ByPathState;
public interface IByPathStateConfig : IConfig
{
    [ConfigItem(Description = "Enables experimental path based storage. Requires fresh sync", DefaultValue = "false")]
    public bool Enabled { get; set; }

    [ConfigItem(Description = "Minimal number of blocks which history state is kept in memory until they are persisted to DB. Maximum is 'InMemHistoryBlocks' + 'PersistenceInterval'.", DefaultValue = "128")]
    public int InMemHistoryBlocks { get; set; }

    [ConfigItem(Description = "Defines how often blocks will be persisted.", DefaultValue = "64")]
    int PersistenceInterval { get; set; }
}
