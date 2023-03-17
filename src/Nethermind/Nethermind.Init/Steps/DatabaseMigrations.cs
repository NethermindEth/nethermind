// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Init.Steps.Migrations;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(InitializeBlockchain), typeof(InitializeNetwork), typeof(ResetDatabaseMigrations))]
    public sealed class DatabaseMigrations : IStep
    {
        private readonly IApiWithNetwork _api;

        public DatabaseMigrations(INethermindApi api)
        {
            _api = api;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            foreach (IDatabaseMigration migration in CreateMigrations())
            {
                migration.Run();
            }

            return Task.CompletedTask;
        }

        private IEnumerable<IDatabaseMigration> CreateMigrations()
        {
            yield return new BloomMigration(_api);
            yield return new ReceiptMigration(_api);
            yield return new ReceiptFixMigration(_api);
            yield return new TotalDifficultyFixMigration(_api, _api.Config<ISyncConfig>());
        }
    }
}
