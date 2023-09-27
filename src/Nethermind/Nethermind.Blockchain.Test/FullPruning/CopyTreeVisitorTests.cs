// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
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
        [TestCase(0, 1)]
        [TestCase(0, 8)]
        [TestCase(1, 1)]
        [TestCase(1, 8)]
        [Timeout(Timeout.MaxTestTime)]
        public void copies_state_between_dbs(int fullPruningMemoryBudgetMb, int maxDegreeOfParallelism)
        {
            TestMemDb trieDb = new();
            TestMemDb clonedDb = new();

            VisitingOptions visitingOptions = new()
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                FullScanMemoryBudget = fullPruningMemoryBudgetMb.MiB(),
            };

            IPruningContext ctx = StartPruning(trieDb, clonedDb);
            CopyDb(ctx, trieDb, visitingOptions, writeFlags: WriteFlags.LowPriority);

            List<byte[]> keys = trieDb.Keys.ToList();
            List<byte[]> values = trieDb.Values.ToList();

            ctx.Commit();

            clonedDb.Count.Should().Be(132);
            clonedDb.Keys.Should().BeEquivalentTo(keys);
            clonedDb.Values.Should().BeEquivalentTo(values);

            clonedDb.KeyWasWrittenWithFlags(keys[0], WriteFlags.LowPriority);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task cancel_coping_state_between_dbs()
        {
            MemDb trieDb = new();
            MemDb clonedDb = new();
            IPruningContext pruningContext = StartPruning(trieDb, clonedDb);
            Task task = Task.Run(() => CopyDb(pruningContext, trieDb));

            pruningContext.CancellationTokenSource.Cancel();

            await task;

            clonedDb.Count.Should().BeLessThan(trieDb.Count);
        }

        private static IPruningContext CopyDb(IPruningContext pruningContext, MemDb trieDb, VisitingOptions? visitingOptions = null, WriteFlags writeFlags = WriteFlags.None)
        {
            LimboLogs logManager = LimboLogs.Instance;
            PatriciaTree trie = Build.A.Trie(trieDb).WithAccountsByIndex(0, 100).TestObject;
            IStateReader stateReader = new StateReader(new TrieStore(trieDb, logManager), new MemDb(), logManager);

            using CopyTreeVisitor copyTreeVisitor = new(pruningContext, writeFlags, logManager);
            stateReader.RunTreeVisitor(copyTreeVisitor, trie.RootHash, visitingOptions);
            return pruningContext;
        }

        private static IPruningContext StartPruning(MemDb trieDb, MemDb clonedDb)
        {
            IRocksDbFactory rocksDbFactory = Substitute.For<IRocksDbFactory>();
            rocksDbFactory.CreateDb(Arg.Any<RocksDbSettings>()).Returns(trieDb, clonedDb);

            FullPruningDb fullPruningDb = new(new RocksDbSettings("test", "test"), rocksDbFactory);
            fullPruningDb.TryStartPruning(out IPruningContext pruningContext);
            return pruningContext;
        }
    }
}
