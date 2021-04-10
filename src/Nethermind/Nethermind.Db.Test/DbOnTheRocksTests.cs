//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    public class DbOnTheRocksTests
    {
        [Test]
        public void Smoke_test()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new ("blocks", GetRocksDbSettings("blocks", "Blocks"), config, LimboLogs.Instance);
            db[new byte[] {1, 2, 3}] = new byte[] {4, 5, 6};
            Assert.AreEqual(new byte[] {4, 5, 6}, db[new byte[] {1, 2, 3}]);
        }

        [Test]
        public void Can_get_all_on_empty()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new ("testIterator", GetRocksDbSettings("testIterator", "TestIterator"), config, LimboLogs.Instance);
            try
            {
                // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
                _ = db.GetAll().ToList();
            }
            finally
            {
                db.Clear();
                db.Dispose();
            }
        }

        [Test]
        public async Task Dispose_while_writing_does_not_cause_access_violation_exception()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new ("testDispose1", GetRocksDbSettings("testDispose1", "TestDispose1"), config, LimboLogs.Instance);

            Task task = new (() =>
            {
                while (true)
                {
                    // ReSharper disable once AccessToDisposedClosure
                    db.Set(Keccak.Zero, new byte[] {1, 2, 3});
                }
                
                // ReSharper disable once FunctionNeverReturns
            });

            task.Start();

            await Task.Delay(100);
            
            db.Dispose();
            
            await Task.Delay(100);
            
            task.Dispose();
        }

        private static RocksDbSettings GetRocksDbSettings(string dbPath, string dbName)
        {
            return new(dbName, dbPath)
            {
                BlockCacheSize = (ulong)1.KiB(),
                CacheIndexAndFilterBlocks = false,
                WriteBufferNumber = 4,
                WriteBufferSize = (ulong)1.KiB()
            };
        }
    }
}
