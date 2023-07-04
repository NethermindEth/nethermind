// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Db
{
    public interface IColumnsDb<TKey> : IDbWithSpan
    {
        IDbWithSpan GetColumnDb(TKey key);
        IEnumerable<TKey> ColumnKeys { get; }
    }
}
