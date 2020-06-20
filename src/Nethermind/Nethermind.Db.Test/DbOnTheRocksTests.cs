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
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Synchronization.Peers;
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
            DbOnTheRocks db = new BlocksRocksDb("blocks", config);
            db[new byte[] {1, 2, 3}] = new byte[] {4, 5, 6};
            Assert.AreEqual(new byte[] {4, 5, 6}, db[new byte[] {1, 2, 3}]);
        }

        [Test]
        public void Can_get_all_on_empty()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new BlocksRocksDb("testIterator", config);
            try
            {
                db.GetAll().ToList();
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
            DbOnTheRocks db = new BlocksRocksDb("testDispose1", config);

            Task task = new Task(() =>
            {
                while (true)
                {
                    db.Set(Keccak.Zero, new byte[] {1, 2, 3});
                }
            });

            task.Start();

            await Task.Delay(100);
            
            db.Dispose();
            
            await Task.Delay(100);
            
            task.Dispose();
        }
    }
}