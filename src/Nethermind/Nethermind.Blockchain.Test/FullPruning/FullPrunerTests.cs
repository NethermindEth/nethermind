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

using System;
using System.Threading;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning
{
    [Parallelizable(ParallelScope.All)]
    public class FullPrunerTests
    {
        [Test]
        public void can_prune()
        {
            TestContext test = CreateTest();

            test.BlockTree.HighestPersistedState.Returns(10);
            test.BlockTree.FindHeader(10).Returns(Build.A.BlockHeader.TestObject);
            IPruningContext pruningContext = Substitute.For<IPruningContext>();
            test.FullPruningDb.TryStartPruning(out Arg.Any<IPruningContext>()).Returns(c =>
            {
                c[0] = pruningContext;
                return true;
            });
            ManualResetEvent manualResetEvent = new(false);
            pruningContext.When(p => p.Commit()).Do(_ => manualResetEvent.Reset());
            
            test.PruningTrigger.Prune += Raise.Event();
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(100));
            pruningContext.Received().Commit();
        }

        private static TestContext CreateTest() => new();

        private class TestContext
        {
            public IFullPruningDb FullPruningDb { get; } = Substitute.For<IFullPruningDb>();
            public IPruningTrigger PruningTrigger { get; } = Substitute.For<IPruningTrigger>();
            public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
            public IStateReader StateReader { get; } = Substitute.For<IStateReader>();
            public FullPruner Pruner { get; }

            public TestContext()
            {
                Pruner = new(FullPruningDb, PruningTrigger, BlockTree, StateReader, LimboLogs.Instance);
            }
        }
    }
}
