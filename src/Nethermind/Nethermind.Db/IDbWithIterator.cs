// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using RocksDbSharp;

namespace Nethermind.Db;
public interface IDbWithIterator : IDb
{
    public Iterator CreateIterator(bool ordered = false, ColumnFamilyHandle? ch = null);
}
