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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;
using Nethermind.Trie;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class StateSyncFeedTests
    {
        [SetUp]
        public void Setup()
        {
            _logManager = LimboLogs.Instance;
            _logger = LimboTraceLogger.Instance;
            TrieScenarios.InitOnce();
        }

        [TearDown]
        public void TearDown()
        {
            (_logger as ConsoleAsyncLogger)?.Flush();
        }

        private ICryptoRandom _cryptoRandom = new CryptoRandom();


        private static StorageTree SetStorage(IDb db, byte i)
        {
            StorageTree remoteStorageTree = new StorageTree(db);
            for (int j = 0; j < i; j++) remoteStorageTree.Set((UInt256) j, new[] {(byte) j, i});

            remoteStorageTree.Commit();
            return remoteStorageTree;
        }

        private class DbContext
        {
            private readonly ILogger _logger;

            public DbContext(ILogger logger)
            {
                _logger = logger;
                RemoteDb = new MemDb();
                LocalDb = new MemDb();
                RemoteStateDb = new StateDb(RemoteDb);
                LocalStateDb = new StateDb(LocalDb);
                LocalCodeDb = new StateDb(LocalDb);
                RemoteCodeDb = new StateDb(RemoteDb);

                RemoteStateTree = new StateTree(RemoteStateDb);
                LocalStateTree = new StateTree(LocalStateDb);
            }

            public StateDb RemoteCodeDb { get; }
            public StateDb LocalCodeDb { get; }
            public MemDb RemoteDb { get; }
            public MemDb LocalDb { get; }
            public StateDb RemoteStateDb { get; }
            public StateDb LocalStateDb { get; }
            public StateTree RemoteStateTree { get; }
            public StateTree LocalStateTree { get; }

            public void CompareTrees(string stage, bool skipLogs = false)
            {
                DbContext dbContext = new DbContext(_logger);
                if (!skipLogs) _logger.Info($"==================== {stage} ====================");
                dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;

                if (!skipLogs) _logger.Info("-------------------- REMOTE --------------------");
                TreeDumper dumper = new TreeDumper();
                dbContext.RemoteStateTree.Accept(dumper, dbContext.RemoteStateTree.RootHash, true);
                string remote = dumper.ToString();
                if (!skipLogs) _logger.Info(remote);
                if (!skipLogs) _logger.Info("-------------------- LOCAL --------------------");
                dumper.Reset();
                dbContext.LocalStateTree.Accept(dumper, dbContext.LocalStateTree.RootHash, true);
                string local = dumper.ToString();
                if (!skipLogs) _logger.Info(local);

                if (stage == "END")
                {
                    Assert.AreEqual(remote, local, $"{remote}{Environment.NewLine}{local}");
                    TrieStatsCollector collector = new TrieStatsCollector(dbContext.LocalCodeDb, new OneLoggerLogManager(_logger));
                    dbContext.LocalStateTree.Accept(collector, dbContext.LocalStateTree.RootHash, true);
                    Assert.AreEqual(0, collector.Stats.MissingCode);
                }

                //            Assert.AreEqual(dbContext._remoteCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
                //            Assert.AreEqual(dbContext._remoteCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
                //
                //            Assert.AreEqual(dbContext._remoteDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
                //            Assert.AreEqual(dbContext._remoteDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
            }

            public void CompareCodeDbs()
            {
                //            Assert.AreEqual(dbContext._remoteCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
                //            Assert.AreEqual(dbContext._remoteCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");

                //            Assert.AreEqual(dbContext._remoteDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
                //            Assert.AreEqual(dbContext._remoteDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
            }
        }

        private static IBlockTree _blockTree;
        private static IBlockTree BlockTree => LazyInitializer.EnsureInitialized(ref _blockTree, () => Build.A.BlockTree().OfChainLength(100).TestObject);

        private class SyncPeerMock : ISyncPeer
        {
            public static Func<IList<Keccak>, Task<byte[][]>> NotPreimage = request =>
            {
                var result = new byte[request.Count][];

                int i = 0;
                foreach (Keccak _ in request) result[i++] = new byte[] {1, 2, 3};

                return Task.FromResult(result);
            };

            public static Func<IList<Keccak>, Task<byte[][]>> EmptyArraysInResponses = request =>
            {
                var result = new byte[request.Count][];

                int i = 0;
                foreach (Keccak _ in request) result[i++] = new byte[0];

                return Task.FromResult(result);
            };

            private readonly StateDb _codeDb;
            private readonly StateDb _stateDb;

            private Func<IList<Keccak>, Task<byte[][]>> _executorResultFunction;

            private Keccak[] _filter;

            public SyncPeerMock(StateDb stateDb, StateDb codeDb, Func<IList<Keccak>, Task<byte[][]>> executorResultFunction = null)
            {
                _stateDb = stateDb;
                _codeDb = codeDb;

                if (executorResultFunction != null) _executorResultFunction = executorResultFunction;

                Node = new Node(TestItem.PublicKeyA, "127.0.0.1", 30302, true);
            }

            public int MaxResponseLength { get; set; } = int.MaxValue;

            public Node Node { get; }
            public string ClientId => "executorMock";
            public Keccak HeadHash { get; set; }
            public long HeadNumber { get; set; }
            public UInt256 TotalDifficulty { get; set; }
            public bool IsInitialized { get; set; }

            public void Disconnect(DisconnectReason reason, string details)
            {
                throw new NotImplementedException();
            }

            public Task<BlockBody[]> GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<BlockHeader> GetHeadBlockHeader(Keccak hash, CancellationToken token)
            {
                return Task.FromResult(BlockTree.Head?.Header);
            }

            public void SendNewBlock(Block block)
            {
                throw new NotImplementedException();
            }

            public void HintNewBlock(Keccak blockHash, long number)
            {
                throw new NotImplementedException();
            }

            public PublicKey Id => Node.Id;

            public void SendNewTransaction(Transaction transaction, bool isPriority)
            {
                throw new NotImplementedException();
            }

            public Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
            {
                if (_executorResultFunction != null) return _executorResultFunction(hashes);

                var responses = new byte[hashes.Count][];

                int i = 0;
                foreach (Keccak item in hashes)
                {
                    if (i >= MaxResponseLength) break;

                    if (_filter == null || _filter.Contains(item)) responses[i] = _stateDb[item.Bytes] ?? _codeDb[item.Bytes];

                    i++;
                }

                return Task.FromResult(responses);
            }

            public void SetFilter(Keccak[] availableHashes)
            {
                _filter = availableHashes;
            }
        }

        private ILogger _logger;

        private ILogManager _logManager;

        private ISyncModeSelector _syncModeSelector;
        private ISyncPeerPool _pool;
        private StateSyncFeed _feed;
        private StateSyncDispatcher _stateSyncDispatcher;

        private void PrepareDownloader(ISyncPeer syncPeer)
        {
            DbContext dbContext = new DbContext(_logger);
            BlockTree blockTree = Build.A.BlockTree().OfChainLength((int) BlockTree.BestSuggestedHeader.Number).TestObject;
            _pool = new SyncPeerPool(blockTree, new NodeStatsManager(new StatsConfig(), LimboLogs.Instance), 25, LimboLogs.Instance);
            _pool.Start();
            _pool.AddPeer(syncPeer);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastSync = true;
            _syncModeSelector = StaticSelector.StateNodesWithFastBlocks;
            _feed = new StateSyncFeed(dbContext.LocalCodeDb, dbContext.LocalStateDb, new MemDb(), _syncModeSelector, blockTree, _logManager);
            _stateSyncDispatcher = new StateSyncDispatcher(_feed, _pool, new StateSyncAllocationStrategyFactory(), _logManager);
        }

        private const int TimeoutLength = 1000;

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(5)]
        public async Task Big_test((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger);
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code0).Bytes] = TrieScenarios.Code0;
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code1).Bytes] = TrieScenarios.Code1;
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code2).Bytes] = TrieScenarios.Code2;
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code3).Bytes] = TrieScenarios.Code3;
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            dbContext.RemoteStateDb.Commit();

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.SetFilter(((MemDb) dbContext.RemoteStateDb.Innermost).Keys.Take(((MemDb) dbContext.RemoteStateDb.Innermost).Keys.Count - 4).Select(k => new Keccak(k)).ToArray());

            dbContext.CompareTrees("BEFORE FIRST SYNC", true);

            PrepareDownloader(mock);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(TimeoutLength));
            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("AFTER FIRST SYNC", true);

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;
            for (byte i = 0; i < 8; i++)
                dbContext.RemoteStateTree
                    .Set(TestItem.Addresses[i], TrieScenarios.AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(1)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code3))
                        .WithChangedStorageRoot(SetStorage(dbContext.RemoteStateDb, i).RootHash));

            dbContext.RemoteStateTree.UpdateRootHash();
            dbContext.RemoteStateTree.Commit();
            dbContext.RemoteStateDb.Commit();

            _pool.WakeUpAll();
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(TimeoutLength));
            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("AFTER SECOND SYNC", true);

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;
            for (byte i = 0; i < 16; i++)
                dbContext.RemoteStateTree
                    .Set(TestItem.Addresses[i], TrieScenarios.AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(2)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code3))
                        .WithChangedStorageRoot(SetStorage(dbContext.RemoteStateDb, (byte) (i % 7)).RootHash));

            dbContext.RemoteStateTree.UpdateRootHash();
            dbContext.RemoteStateTree.Commit();
            dbContext.RemoteStateDb.Commit();

            _pool.WakeUpAll();
            mock.SetFilter(null);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(TimeoutLength));
            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("END");
            dbContext.CompareCodeDbs();
        }

        public static (string Name, Action<StateTree, StateDb, StateDb> Action)[] Scenarios => TrieScenarios.Scenarios;

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(5)]
        public async Task Can_download_a_full_state((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            dbContext.RemoteStateDb.Commit();

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            PrepareDownloader(mock);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            Task syncNode = _stateSyncDispatcher.Start(CancellationToken.None);

            Task first = await Task.WhenAny(syncNode, Task.Delay(TimeoutLength));
            if (first == syncNode)
                if (syncNode.IsFaulted)
                    throw syncNode.Exception;

            dbContext.LocalStateDb.Commit();
            dbContext.CompareTrees("END");
        }

        [Test]
        public async Task Can_download_an_empty_tree()
        {
            DbContext dbContext = new DbContext(_logger);
            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            PrepareDownloader(mock);
            _feed.ResetStateRoot(1000, dbContext.RemoteStateTree.RootHash);
            await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(TimeoutLength));

            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(5)]
        public async Task Can_download_in_multiple_connections((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            dbContext.RemoteStateDb.Commit();

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.SetFilter(new[] {dbContext.RemoteStateTree.RootHash});

            PrepareDownloader(mock);

            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(TimeoutLength));
            dbContext.LocalStateDb.Commit();

            _pool.WakeUpAll();

            mock.SetFilter(null);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(TimeoutLength));
            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(5)]
        public async Task Can_download_when_executor_sends_shorter_responses((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            dbContext.RemoteStateDb.Commit();

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.MaxResponseLength = 1;

            PrepareDownloader(mock);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(TimeoutLength));
            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(5)]
        public async Task Can_download_with_moving_target((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            dbContext.RemoteStateDb.Commit();

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.SetFilter(((MemDb) dbContext.RemoteStateDb.Innermost).Keys.Take(((MemDb) dbContext.RemoteStateDb.Innermost).Keys.Count - 1).Select(k => new Keccak(k)).ToArray());

            dbContext.CompareTrees("BEFORE FIRST SYNC");

            PrepareDownloader(mock);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(TimeoutLength));
            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("AFTER FIRST SYNC");

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;
            dbContext.RemoteStateTree.Set(TestItem.AddressA, TrieScenarios.AccountJustState0.WithChangedBalance(123.Ether()));
            dbContext.RemoteStateTree.Set(TestItem.AddressB, TrieScenarios.AccountJustState1.WithChangedBalance(123.Ether()));
            dbContext.RemoteStateTree.Set(TestItem.AddressC, TrieScenarios.AccountJustState2.WithChangedBalance(123.Ether()));

            dbContext.CompareTrees("BEFORE ROOT HASH UPDATE");

            dbContext.RemoteStateTree.UpdateRootHash();

            dbContext.CompareTrees("BEFORE COMMIT");

            dbContext.RemoteStateTree.Commit();
            dbContext.RemoteStateDb.Commit();

            _pool.WakeUpAll();

            mock.SetFilter(null);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(TimeoutLength));
            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("END");
            dbContext.CompareCodeDbs();
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(5)]
        public async Task Dependent_branch_counter_is_zero_and_leaf_is_short((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            dbContext.RemoteStateDb.Commit();

            StorageTree remoteStorageTree = new StorageTree(dbContext.RemoteDb);
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb000"), new byte[] {1});
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb111"), new byte[] {1});
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeb000000000000000000000000000000000000000000"), new byte[] {1});
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeb111111111111111111111111111111111111111111"), new byte[] {1});
            remoteStorageTree.Commit();

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit();

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            PrepareDownloader(mock);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            Task syncNode = _stateSyncDispatcher.Start(CancellationToken.None);

            Task first = await Task.WhenAny(syncNode, Task.Delay(TimeoutLength));
            if (first == syncNode)
                if (syncNode.IsFaulted)
                    throw syncNode.Exception;

            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(5)]
        public async Task Scenario_plus_one_code((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            dbContext.RemoteStateDb.Commit();

            dbContext.RemoteCodeDb.Set(Keccak.Compute(TrieScenarios.Code0), TrieScenarios.Code0);
            dbContext.RemoteCodeDb.Commit();

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0)));
            dbContext.RemoteStateTree.Commit();

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            PrepareDownloader(mock);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            Task syncNode = _stateSyncDispatcher.Start(CancellationToken.None);

            Task first = await Task.WhenAny(syncNode, Task.Delay(TimeoutLength));
            if (first == syncNode)
                if (syncNode.IsFaulted)
                    throw syncNode.Exception;

            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(5)]
        public async Task Scenario_plus_one_code_one_storage((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            dbContext.RemoteStateDb.Commit();

            dbContext.RemoteCodeDb.Set(Keccak.Compute(TrieScenarios.Code0), TrieScenarios.Code0);
            dbContext.RemoteCodeDb.Commit();

            StorageTree remoteStorageTree = new StorageTree(dbContext.RemoteDb);
            remoteStorageTree.Set((UInt256) 1, new byte[] {1});
            remoteStorageTree.Commit();

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0)).WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit();

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            PrepareDownloader(mock);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            Task syncNode = _stateSyncDispatcher.Start(CancellationToken.None);

            Task first = await Task.WhenAny(syncNode, Task.Delay(TimeoutLength));
            if (first == syncNode)
                if (syncNode.IsFaulted)
                    throw syncNode.Exception;

            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(5)]
        public async Task Scenario_plus_one_storage((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            dbContext.RemoteStateDb.Commit();

            StorageTree remoteStorageTree = new StorageTree(dbContext.RemoteDb);
            remoteStorageTree.Set((UInt256) 1, new byte[] {1});
            remoteStorageTree.Commit();

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit();

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            PrepareDownloader(mock);
            _feed.ResetStateRoot(1024, dbContext.RemoteStateTree.RootHash);
            Task syncNode = _stateSyncDispatcher.Start(CancellationToken.None);

            Task first = await Task.WhenAny(syncNode, Task.Delay(TimeoutLength));
            if (first == syncNode)
                if (syncNode.IsFaulted)
                    throw syncNode.Exception;

            dbContext.LocalStateDb.Commit();

            dbContext.CompareTrees("END");
        }

        // [Test, Retry(5)]
        // public async Task Silences_bad_peers()
        // {
        //     DbContext dbContext = new DbContext(_logger);
        //     SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb, SyncPeerMock.NotPreimage);
        //     PrepareDownloader(mock);
        //     _feed.SetNewStateRoot(1024, Keccak.Compute("the_peer_has_no_data"));
        //     _feed.Activate();
        //     await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(1000)).Unwrap()
        //         .ContinueWith(t =>
        //         {
        //             Assert.AreEqual(0, _pool.InitializedPeers.Count(p => p.CanBeAllocated(AllocationContexts.All)));
        //         });
        // }

        // [Test]
        // [Retry(5)]
        // public async Task Silences_when_peer_sends_empty_byte_arrays()
        // {
        //     DbContext dbContext = new DbContext(_logger);
        //     SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb, SyncPeerMock.EmptyArraysInResponses);
        //     PrepareDownloader(mock);
        //     _feed.SetNewStateRoot(1024, Keccak.Compute("the_peer_has_no_data"));
        //     _feed.Activate();
        //     await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(1000)).Unwrap()
        //         .ContinueWith(t =>
        //         {
        //             _pool.InitializedPeers.Count(p => p.CanBeAllocated(AllocationContexts.All)).Should().Be(0);
        //         });
        // }
    }
}