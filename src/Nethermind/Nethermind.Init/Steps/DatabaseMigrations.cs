// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Init.Steps.Migrations;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitTxTypesAndRlp), typeof(InitDatabase), typeof(InitializeBlockchain), typeof(InitializeNetwork))]
    public sealed class DatabaseMigrations(INethermindApi api) : IStep
    {
        private readonly IApiWithNetwork _api = api;

        public async Task Execute(CancellationToken cancellationToken)
        {
            foreach (IDatabaseMigration migration in CreateMigrations())
            {
                await migration.Run(cancellationToken);
            }
        }

        private IEnumerable<IDatabaseMigration> CreateMigrations()
        {
            yield return new BloomMigration(_api);
            yield return new ReceiptMigration(_api);
            yield return new ReceiptFixMigration(_api);
            //yield return new LogIndexMigration(_api);
            yield return new TotalDifficultyFixMigration(_api.ChainLevelInfoRepository, _api.BlockTree, _api.Config<ISyncConfig>(), _api.LogManager);
        }
    }
}
