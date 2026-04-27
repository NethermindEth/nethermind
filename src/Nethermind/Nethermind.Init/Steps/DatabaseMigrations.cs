// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps.Migrations;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitTxTypesAndRlp), typeof(InitDatabase), typeof(InitializeBlockchain), typeof(InitializeNetwork))]
    public sealed class DatabaseMigrations(IEnumerable<IDatabaseMigration> migrations) : IStep
    {
        public async Task Execute(CancellationToken cancellationToken)
        {
            foreach (IDatabaseMigration migration in migrations)
            {
                await migration.Run(cancellationToken);
            }
        }
    }
}
