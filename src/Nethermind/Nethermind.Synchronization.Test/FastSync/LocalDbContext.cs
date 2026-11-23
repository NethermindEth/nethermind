// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Db.WasFlushed, Is.True);
            Assert.That(CodeDb.WasFlushed, Is.True);
        }
    }

    public void CompareTrees(RemoteDbContext remote, ILogger logger, string stage)
    {
        (string remoteStr, string localStr) = Dump(remote, logger, stage);

        Assert.That(localStr, Is.EqualTo(remoteStr), $"{stage}{Environment.NewLine}{remoteStr}{Environment.NewLine}{localStr}");
        TrieStatsCollector collector = new(CodeDb, LimboLogs.Instance);
        StateTree.Accept(new NodeRecordVisitorAdapter<TrieStatsCollector.Context>(collector), StateTree.RootHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(collector.Stats.MissingNodes, Is.EqualTo(0), stage);
            Assert.That(collector.Stats.MissingCode, Is.EqualTo(0), stage);
        }
    }

    public void DumpTrees(RemoteDbContext remote, ILogger logger, string stage) => Dump(remote, logger, stage);

    private (string Remote, string Local) Dump(RemoteDbContext remote, ILogger logger, string stage)
    {
        logger.Info($"==================== {stage} ====================");
        StateTree.RootHash = remote.StateTree.RootHash;

        logger.Info("-------------------- REMOTE --------------------");
        TreeDumper dumper = new();
        remote.StateTree.Accept(new NodeRecordVisitorAdapter<OldStyleTrieVisitContext>(dumper), remote.StateTree.RootHash);
        string remoteStr = dumper.ToString();
        logger.Info(remoteStr);
        logger.Info("-------------------- LOCAL --------------------");
        dumper.Reset();
        StateTree.Accept(new NodeRecordVisitorAdapter<OldStyleTrieVisitContext>(dumper), StateTree.RootHash);
        string localStr = dumper.ToString();
        logger.Info(localStr);
        return (remoteStr, localStr);
    }

    public void DeleteStateRoot() =>
        NodeStorage.Set(null, TreePath.Empty, RootHash, null);
}
