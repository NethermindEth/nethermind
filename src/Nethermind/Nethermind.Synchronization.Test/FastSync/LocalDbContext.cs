// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class LocalDbContext : IStateSyncTestOperation
{
    private TestMemDb CodeDb { get; }
    private TestMemDb Db { get; }
    private INodeStorage NodeStorage { get; }
    private ITrieStore TrieStore { get; }
    private StateTree StateTree { get; }

    public LocalDbContext(
        [KeyFilter(DbNames.Code)] IDb codeDb,
        [KeyFilter(DbNames.State)] IDb stateDb,
        INodeStorage nodeStorage,
        ILogManager logManager)
    {
        CodeDb = (TestMemDb)codeDb;
        Db = (TestMemDb)stateDb;
        NodeStorage = nodeStorage;
        TrieStore = TestTrieStoreFactory.Build(nodeStorage, logManager);
        StateTree = new(TrieStore.GetTrieStore(null), logManager);
    }

    public Hash256 RootHash => StateTree.RootHash;

    public void UpdateRootHash() => StateTree.UpdateRootHash();

    public void SetAccountsAndCommit(params (Hash256 Address, Account? Account)[] accounts)
    {
        foreach ((Hash256? address, Account? account) in accounts)
            StateTree.Set(address, account);
        StateTree.Commit();
    }

    public void AssertFlushed()
    {
        Db.WasFlushed.Should().BeTrue();
        CodeDb.WasFlushed.Should().BeTrue();
    }

    public void CompareTrees(RemoteDbContext remote, ILogger logger, string stage, bool skipLogs = false)
    {
        if (!skipLogs) logger.Info($"==================== {stage} ====================");
        StateTree.RootHash = remote.StateTree.RootHash;

        if (!skipLogs) logger.Info("-------------------- REMOTE --------------------");
        TreeDumper dumper = new();
        dumper.Traverse(remote.StateTree.RootHash, remote.TrieStore);
        string remoteStr = dumper.ToString();
        if (!skipLogs) logger.Info(remoteStr);
        if (!skipLogs) logger.Info("-------------------- LOCAL --------------------");
        dumper.Reset();
        dumper.Traverse(StateTree.RootHash, TrieStore);
        string localStr = dumper.ToString();
        if (!skipLogs) logger.Info(localStr);

        if (stage == "END")
        {
            Assert.That(localStr, Is.EqualTo(remoteStr), $"{stage}{Environment.NewLine}{remoteStr}{Environment.NewLine}{localStr}");
            TrieStatsCollector collector = new(CodeDb, LimboLogs.Instance);
            collector.Traverse(StateTree.RootHash, TrieStore);
            Assert.That(collector.Stats.MissingNodes, Is.EqualTo(0));
            Assert.That(collector.Stats.MissingCode, Is.EqualTo(0));
        }
    }

    public void DeleteStateRoot() =>
        NodeStorage.Set(null, TreePath.Empty, RootHash, null);
}
