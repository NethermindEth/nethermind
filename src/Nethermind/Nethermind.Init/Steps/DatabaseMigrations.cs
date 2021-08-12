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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
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
            foreach (var migration in CreateMigrations())
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
        }
    }
}
