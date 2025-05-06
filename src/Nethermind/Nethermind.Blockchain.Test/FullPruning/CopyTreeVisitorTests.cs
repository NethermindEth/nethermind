// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

namespace Nethermind.Blockchain.Test.FullPruning;

[Parallelizable(ParallelScope.Self)]
[TestFixture(INodeStorage.KeyScheme.HalfPath)]
[TestFixture(INodeStorage.KeyScheme.Hash)]
public class CopyTreeVisitorTests
{
    private readonly INodeStorage.KeyScheme _keyScheme;

    public CopyTreeVisitorTests(INodeStorage.KeyScheme scheme)
    {
        _keyScheme = scheme;
    }

    [TestCase(0, 1)]
    [TestCase(0, 8)]
    [TestCase(1, 1)]
    [TestCase(1, 8)]
    [MaxTime(Timeout.MaxTestTime)]
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
        CopyDb(ctx, CancellationToken.None, trieDb, visitingOptions, writeFlags: WriteFlags.LowPriority);

        List<byte[]> keys = trieDb.Keys.ToList();
        List<byte[]> values = trieDb.Values.ToList();

        ctx.Commit();

        clonedDb.Count.Should().Be(132);
        clonedDb.Keys.Should().BeEquivalentTo(keys);
        clonedDb.Values.Should().BeEquivalentTo(values);

        clonedDb.KeyWasWrittenWithFlags(keys[0], WriteFlags.LowPriority);
        trieDb.KeyWasReadWithFlags(keys[0], ReadFlags.SkipDuplicateRead | ReadFlags.HintReadAhead);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void cancel_coping_state_between_dbs()
    {
        MemDb trieDb = new();
        MemDb clonedDb = new();
        IPruningContext pruningContext = StartPruning(trieDb, clonedDb);

        CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel();

        CopyDb(pruningContext, cts.Token, trieDb);

        clonedDb.Count.Should().BeLessThan(trieDb.Count);
    }

    private IPruningContext CopyDb(IPruningContext pruningContext, CancellationToken cancellationToken, MemDb trieDb, VisitingOptions? visitingOptions = null, WriteFlags writeFlags = WriteFlags.None)
    {
        LimboLogs logManager = LimboLogs.Instance;
        PatriciaTree trie = Build.A.Trie(new NodeStorage(trieDb, _keyScheme)).WithAccountsByIndex(0, 100).TestObject;
        IStateReader stateReader = new StateReader(TestTrieStoreFactory.Build(trieDb, logManager), new MemDb(), logManager);

        if (_keyScheme == INodeStorage.KeyScheme.Hash)
        {
            NodeStorage nodeStorage = new NodeStorage(pruningContext, _keyScheme);
            using CopyTreeVisitor<NoopTreePathContextWithStorage> copyTreeVisitor = new(nodeStorage, writeFlags, logManager, cancellationToken);
            stateReader.RunTreeVisitor(copyTreeVisitor, trie.RootHash, visitingOptions);
            copyTreeVisitor.Finish();
        }
        else
        {
            NodeStorage nodeStorage = new NodeStorage(pruningContext, _keyScheme);
            using CopyTreeVisitor<TreePathContextWithStorage> copyTreeVisitor = new(nodeStorage, writeFlags, logManager, cancellationToken);
            stateReader.RunTreeVisitor(copyTreeVisitor, trie.RootHash, visitingOptions);
            copyTreeVisitor.Finish();
        }

        return pruningContext;
    }

    private static IPruningContext StartPruning(MemDb trieDb, MemDb clonedDb)
    {
        IDbFactory dbFactory = Substitute.For<IDbFactory>();
        dbFactory.CreateDb(Arg.Any<DbSettings>()).Returns(trieDb, clonedDb);

        FullPruningDb fullPruningDb = new(new DbSettings("test", "test"), dbFactory);
        fullPruningDb.TryStartPruning(out IPruningContext pruningContext);
        return pruningContext;
    }
}
