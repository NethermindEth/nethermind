// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.RocksDbExtractor.ProviderDecoders.RocksDb
{
    internal class ProviderDepositApprovalsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "ProviderDepositApprovals";

        public ProviderDepositApprovalsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager)
            : base(basePath, "providerDepositApprovals", dbConfig, logManager)
        {
        }
    }
}
