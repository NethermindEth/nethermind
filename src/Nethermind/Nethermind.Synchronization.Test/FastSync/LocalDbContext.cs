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
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class LocalDbContext(
    [KeyFilter(DbNames.Code)] IDb codeDb,
    [KeyFilter(DbNames.State)] IDb stateDb,
    INodeStorage nodeStorage,
    ILogManager logManager)
    : IStateSyncTestOperation
{
    private TestMemDb CodeDb { get; } = (TestMemDb)codeDb;
    private TestMemDb Db { get; } = (TestMemDb)stateDb;
    private INodeStorage NodeStorage { get; } = nodeStorage;
    private StateTree StateTree { get; } = new(TestTrieStoreFactory.Build(nodeStorage, logManager), logManager);

    public Hash256 RootHash
    {
        get => StateTree.RootHash;
    }

    public void UpdateRootHash() => StateTree.UpdateRootHash();

    public void SetAccountsAndCommit(params (Hash256 Address, Account? Account)[] accounts)
    {
        foreach (var (address, account) in accounts)
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
        TreeDumper dumper = new TreeDumper();
        remote.StateTree.Accept(dumper, remote.StateTree.RootHash);
        string remoteStr = dumper.ToString();
        if (!skipLogs) logger.Info(remoteStr);
        if (!skipLogs) logger.Info("-------------------- LOCAL --------------------");
        dumper.Reset();
        StateTree.Accept(dumper, StateTree.RootHash);
        string localStr = dumper.ToString();
        if (!skipLogs) logger.Info(localStr);

        if (stage == "END")
        {
            Assert.That(localStr, Is.EqualTo(remoteStr), $"{stage}{Environment.NewLine}{remoteStr}{Environment.NewLine}{localStr}");
            TrieStatsCollector collector = new(CodeDb, LimboLogs.Instance);
            StateTree.Accept(collector, StateTree.RootHash);
            Assert.That(collector.Stats.MissingNodes, Is.EqualTo(0));
            Assert.That(collector.Stats.MissingCode, Is.EqualTo(0));
        }
    }

    public void DeleteStateRoot()
    {
        NodeStorage.Set(null, TreePath.Empty, RootHash, null);
    }
}
