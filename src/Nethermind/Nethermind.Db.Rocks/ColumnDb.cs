// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks;

/// <summary>One column family of a <see cref="ColumnsDb{T}"/>.</summary>
public class ColumnDb(RocksDbInstance instance, string name, IRocksDbConfig config, ILogManager logManager)
    : DbOnTheRocks(instance, instance.Db.GetColumnFamily(name), name, config, logManager)
{
    /// <summary>
    /// Clearing a single column family is not supported; it shares the underlying database with the other columns.
    /// </summary>
    /// <exception cref="NotSupportedException">Always thrown; clearing a single column family is not supported.</exception>
    public override void Clear() => throw new NotSupportedException();
}
