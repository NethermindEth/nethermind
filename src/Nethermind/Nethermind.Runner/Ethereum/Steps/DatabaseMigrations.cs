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

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Runner.Ethereum.Steps.Migrations;
using Nethermind.Store.Bloom;
using Timer = System.Timers.Timer;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(InitializeBlockchain), typeof(InitializeNetwork), typeof(ResetDatabaseMigrations))]
    public class DatabaseMigrations : IStep
    {
        private readonly EthereumRunnerContext _context;

        public DatabaseMigrations(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute()
        {
            foreach (var migration in CreateMigrations())
            {
                migration.Run();
            }
            
            return Task.CompletedTask;
        }

        private IEnumerable<IDatabaseMigration> CreateMigrations()
        {
            yield return new BloomMigration(_context);
            yield return new ReceiptMigration(_context);
        }
    }
}