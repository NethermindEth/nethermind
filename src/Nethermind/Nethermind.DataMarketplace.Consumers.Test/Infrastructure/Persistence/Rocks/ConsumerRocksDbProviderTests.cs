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

using System.IO;
using FluentAssertions;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence.Rocks
{
    [TestFixture]
    [Parallelizable(ParallelScope.Default)]
    public class ConsumerRocksDbProviderTests
    {
        [Test]
        public void Create_dispose()
        {
            ConsumerRocksDbProvider provider = new ConsumerRocksDbProvider(Path.GetTempPath(), DbConfig.Default, LimboLogs.Instance);
            ClearTemp(provider);
            provider.Dispose();
        }

        private static void ClearTemp(ConsumerRocksDbProvider provider)
        {
            provider.DepositsDb.Clear();
            provider.ConsumerReceiptsDb.Clear();
            provider.ConsumerSessionsDb.Clear();
            provider.ConsumerDepositApprovalsDb.Clear();
        }

        [Test]
        public void No_copy_paste_error()
        {
            ConsumerRocksDbProvider provider =
                new ConsumerRocksDbProvider(Path.GetTempPath(), DbConfig.Default, LimboLogs.Instance);
            provider.DepositsDb.Should().NotBeSameAs(provider.ConsumerReceiptsDb);
            provider.DepositsDb.Should().NotBeSameAs(provider.ConsumerSessionsDb);
            provider.DepositsDb.Should().NotBeSameAs(provider.ConsumerDepositApprovalsDb);
            provider.ConsumerReceiptsDb.Should().NotBeSameAs(provider.ConsumerSessionsDb);
            provider.ConsumerReceiptsDb.Should().NotBeSameAs(provider.ConsumerDepositApprovalsDb);
            provider.ConsumerSessionsDb.Should().NotBeSameAs(provider.ConsumerDepositApprovalsDb);
            
            ClearTemp(provider);
            provider.Dispose();
        }
        
        [Test]
        public void Not_returning_new_db_on_each_request()
        {
            ConsumerRocksDbProvider provider =
                new ConsumerRocksDbProvider(Path.GetTempPath(), DbConfig.Default, LimboLogs.Instance);
            provider.DepositsDb.Should().BeSameAs(provider.DepositsDb);
            provider.ConsumerSessionsDb.Should().BeSameAs(provider.ConsumerSessionsDb);
            provider.ConsumerReceiptsDb.Should().BeSameAs(provider.ConsumerReceiptsDb);
            provider.ConsumerDepositApprovalsDb.Should().BeSameAs(provider.ConsumerDepositApprovalsDb);
            
            ClearTemp(provider);
            provider.Dispose();
        }
    }
}