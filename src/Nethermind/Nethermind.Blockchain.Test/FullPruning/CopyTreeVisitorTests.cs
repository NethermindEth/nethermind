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
// 

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning
{
    [Parallelizable(ParallelScope.All)]
    public class CopyTreeVisitorTests
    {
        [Test]
        public void copies_state_between_dbs()
        {
            MemDb trieDb = new MemDb();
            MemDb clonedDb = new MemDb();
            
            CopyDb(trieDb, clonedDb);

            clonedDb.Count.Should().Be(132);
            clonedDb.Keys.Should().BeEquivalentTo(trieDb.Keys);
            clonedDb.Values.Should().BeEquivalentTo(trieDb.Values);
        }
        
        [Test]
        public async Task cancel_coping_state_between_dbs()
        {
            MemDb trieDb = new();
            MemDb clonedDb = new();
            IPruningContext? pruningContext = null;
            Task task = Task.Run(() => pruningContext = CopyDb(trieDb, clonedDb));
            
            pruningContext?.CancellationTokenSource.Cancel();
            
            await task;

            clonedDb.Count.Should().BeLessThan(trieDb.Count);
        }

        private static IPruningContext CopyDb(MemDb trieDb, MemDb clonedDb)
        {
            IRocksDbFactory rocksDbFactory = Substitute.For<IRocksDbFactory>();
            rocksDbFactory.CreateDb(Arg.Any<RocksDbSettings>()).Returns(trieDb, clonedDb);

            FullPruningDb fullPruningDb = new(new RocksDbSettings("Test", "Test"), rocksDbFactory);
            fullPruningDb.TryStartPruning(out IPruningContext pruningContext);

            LimboLogs logManager = LimboLogs.Instance;
            PatriciaTree trie = Build.A.Trie(trieDb).WithAccountsByIndex(0, 100).TestObject;
            IStateReader stateReader = new StateReader(new TrieStore(trieDb, logManager), new MemDb(), logManager);

            using CopyTreeVisitor copyTreeVisitor = new(pruningContext, logManager);
            stateReader.RunTreeVisitor(copyTreeVisitor, trie.RootHash);
            return pruningContext;
        }
    }
}
