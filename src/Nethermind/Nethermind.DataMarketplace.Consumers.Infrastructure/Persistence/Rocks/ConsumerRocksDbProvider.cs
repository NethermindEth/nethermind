/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Databases;
using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks
{
    public class ConsumerRocksDbProvider : IConsumerDbProvider
    {
        public IDb ConsumerDepositApprovalsDb { get; }
        public IDb ConsumerReceiptsDb { get; }
        public IDb ConsumerSessionsDb { get; }
        public IDb DepositsDb { get; }

        public ConsumerRocksDbProvider(string basePath, IDbConfig dbConfig, ILogManager logManager)
        {
            ConsumerDepositApprovalsDb = new ConsumerDepositApprovalsRocksDb(basePath, dbConfig, logManager);
            ConsumerReceiptsDb = new ConsumerReceiptsRocksDb(basePath, dbConfig, logManager);
            ConsumerSessionsDb = new ConsumerSessionsRocksDb(basePath, dbConfig, logManager);
            DepositsDb = new DepositsRocksDb(basePath, dbConfig, logManager);
        }

        public void Dispose()
        {
            ConsumerDepositApprovalsDb?.Dispose();
            ConsumerReceiptsDb?.Dispose();
            ConsumerSessionsDb?.Dispose();
            DepositsDb?.Dispose();
        }
    }
}