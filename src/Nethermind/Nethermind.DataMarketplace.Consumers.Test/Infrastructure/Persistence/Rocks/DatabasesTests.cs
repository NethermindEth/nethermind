//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Databases;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence.Rocks
{
    [TestFixture]
    [Parallelizable(ParallelScope.Default)]
    public class DatabasesTests
    {
        [Test]
        public void SmokeTest()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), nameof(DatabasesTests), nameof(SmokeTest));
            DbConfig config = DbConfig.Default;
            TestDb(new ConsumerDepositApprovalsRocksDb(tempPath, config, LimboLogs.Instance));
            TestDb(new ConsumerSessionsRocksDb(tempPath, config, LimboLogs.Instance));
            TestDb(new ConsumerReceiptsRocksDb(tempPath, config, LimboLogs.Instance));
            TestDb(new DepositsRocksDb(tempPath, config, LimboLogs.Instance));
        }

        private static void TestDb<T>(T db) where T : DbOnTheRocks
        {
            try
            {
                db.Set(Keccak.Zero, Array.Empty<byte>());
                db.Get(Keccak.Zero);
                db.Name.Should().Be(typeof(T).Name.Replace("RocksDb", string.Empty));
            }
            finally
            {
                db.Dispose();
                db.Clear();
            }
        }
    }
}