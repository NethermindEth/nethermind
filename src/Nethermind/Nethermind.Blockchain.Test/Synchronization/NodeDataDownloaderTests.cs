/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    public class NodeDataDownloaderTests
    {
        private static readonly byte[] Code0 = {0, 0};
        private static readonly byte[] Code1 = {0, 1};
        private static readonly byte[] Code2 = {0, 2};
        private static readonly byte[] Code3 = {0, 3};

        private static Account Empty;
        private static Account AccountJustState0;
        private static Account AccountJustState1;
        private static Account AccountJustState2;
        private static Account Account0;
        private static Account Account1;
        private static Account Account2;
        private static Account Account3;

        public static (string Name, Action<StateTree, StateDb, MemDb> Action)[] Scenarios = InitScenarios();

        private static (string Name, Action<StateTree, StateDb, MemDb> Action)[] InitScenarios()
        {
            return new (string, Action<StateTree, StateDb, MemDb>)[]
            {
                ("set_3_via_address", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(TestItem.AddressA, Account0);
                    tree.Set(TestItem.AddressB, Account0);
                    tree.Set(TestItem.AddressC, Account0);
                    tree.Commit();
                }),
                ("set_3_via_hash", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), Account0);
                    tree.Commit();
                }),
                ("set_3_delete_1", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                    tree.Commit();
                }),
                ("set_3_delete_2", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                    tree.Commit();
                }),
                ("set_3_delete_all", (tree, stateDb, codeDb) =>
                {
//                    SetStorage(stateDb);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
                    tree.Commit();
                }),
                ("extension_read_full_match", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), Account1);
                    Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"));
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("extension_read_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), Account1);
                    Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"));
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("extension_new_branch", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    codeDb[Keccak.Compute(Code2).Bytes] = Code2;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), Account1);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), Account2);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("just_state", (tree, stateDb, codeDb) =>
                {
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), AccountJustState0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), AccountJustState1);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), AccountJustState2);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("extension_delete_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), Account1);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd"), null);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("extenson_create_new_extension", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    codeDb[Keccak.Compute(Code2).Bytes] = Code2;
                    codeDb[Keccak.Compute(Code3).Bytes] = Code3;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), Account1);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000"), Account2);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111"), Account3);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("leaf_new_value", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), Account1);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("leaf_no_change", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("leaf_delete", (tree, stateDb, codeDb) =>
                {
//                    SetStorage(stateDb);
                    tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), null);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("leaf_delete_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    tree.Set(new Keccak("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("leaf_update_extension", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000"), Account1);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("leaf_read", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    Account account = tree.Get(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"));
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("leaf_update_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), Account0);
                    Account account = tree.Get(new Keccak("111111111111111111111111111111111111111111111111111111111ddddddd"));
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("branch_update_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    codeDb[Keccak.Compute(Code2).Bytes] = Code2;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), Account1);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), Account2);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("branch_read_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), Account1);
                    Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"));
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
                ("branch_delete_missing", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    codeDb[Keccak.Compute(Code1).Bytes] = Code1;
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), Account0);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), Account1);
                    tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), null);
                    tree.UpdateRootHash();
                    Keccak rootHash = tree.RootHash;
                    tree.Commit();
                }),
            };
        }

        private ICryptoRandom _cryptoRandom = new CryptoRandom();
        private MemDb _remoteCodeDb;
        private MemDb _localCodeDb;
        private MemDb _remoteDb;
        private MemDb _localDb;
        private StateDb _remoteStateDb;
        private StateDb _localStateDb;
        private StateTree _remoteStateTree;
        private StateTree _localStateTree;

        static NodeDataDownloaderTests()
        {
            StorageTree remoteStorageTree = SetStorage(new MemDb());
            Keccak storageRoot = remoteStorageTree.RootHash;

            Empty = Build.An.Account.WithBalance(0).TestObject;
            Account0 = Build.An.Account.WithBalance(1).WithCode(Code0).WithStorageRoot(storageRoot).TestObject;
            Account1 = Build.An.Account.WithBalance(2).WithCode(Code1).WithStorageRoot(storageRoot).TestObject;
            Account2 = Build.An.Account.WithBalance(3).WithCode(Code2).WithStorageRoot(storageRoot).TestObject;
            Account3 = Build.An.Account.WithBalance(4).WithCode(Code3).WithStorageRoot(storageRoot).TestObject;
            
            AccountJustState0 = Build.An.Account.WithBalance(1).TestObject;
            AccountJustState1 = Build.An.Account.WithBalance(2).TestObject;
            AccountJustState2 = Build.An.Account.WithBalance(3).TestObject;
        }

        private static StorageTree SetStorage(IDb db)
        {
            StorageTree remoteStorageTree = new StorageTree(db);

            remoteStorageTree.Set((UInt256) 1, new byte[] {1});
            remoteStorageTree.Set((UInt256) 2, new byte[] {2});
            remoteStorageTree.Set((UInt256) 3, new byte[] {3});
            remoteStorageTree.Set((UInt256) 4, new byte[] {4});
            remoteStorageTree.Set((UInt256) 1005, new byte[] {5});
            remoteStorageTree.Set((UInt256) 1006, new byte[] {6});
            remoteStorageTree.Set((UInt256) 1007, new byte[] {7});
            remoteStorageTree.Set((UInt256) 1008, new byte[] {8});

            remoteStorageTree.Commit();
            return remoteStorageTree;
        }

        [SetUp]
        public void Setup()
        {
            _logger = new ConsoleAsyncLogger(LogLevel.Trace);
            _logManager = new OneLoggerLogManager(_logger);

            _remoteDb = new MemDb();
            _localDb = new MemDb();
            _remoteStateDb = new StateDb(_remoteDb);
            _localStateDb = new StateDb(_localDb);
            _localCodeDb = new MemDb();
            _remoteCodeDb = new MemDb();

            _remoteStateTree = new StateTree(_remoteStateDb);
            _localStateTree = new StateTree(_localStateDb);
        }

        [TearDown]
        public void TearDown()
        {
            _logger.Flush();
        }

        private class ExecutorMock : INodeDataRequestExecutor
        {
            private readonly StateDb _stateDb;
            private readonly MemDb _codeDb;

            public ExecutorMock(StateDb stateDb, MemDb codeDb)
            {
                _stateDb = stateDb;
                _codeDb = codeDb;
            }

            private Keccak[] _filter;
            public int MaxResponseLength { get; set; } = int.MaxValue;

            public void SetFilter(Keccak[] availableHashes)
            {
                _filter = availableHashes;
            }

            public Task<StateSyncBatch> ExecuteRequest(CancellationToken token, StateSyncBatch batch)
            {
                batch.Responses = new byte[Math.Min(MaxResponseLength, batch.StateSyncs.Length)][];

                int i = 0;
                foreach (StateSyncItem item in batch.StateSyncs)
                {
                    if (i >= MaxResponseLength)
                    {
                        break;
                    }

                    if (_filter != null && !_filter.Contains(item.Hash))
                    {
                        continue;
                    }

                    batch.Responses[i++] = _stateDb[item.Hash.Bytes] ?? _codeDb[item.Hash.Bytes];
                }

                return Task.FromResult(batch);
            }
        }

        private class MaliciousExecutorMock : INodeDataRequestExecutor
        {
            public static Func<StateSyncBatch, Task<StateSyncBatch>> NotPreimage = request =>
            {
                request.Responses = new byte[request.StateSyncs.Length][];

                int i = 0;
                foreach (StateSyncItem _ in request.StateSyncs)
                {
                    request.Responses[i++] = new byte[] {1, 2, 3};
                }

                return Task.FromResult(request);
            };

            public static Func<StateSyncBatch, Task<StateSyncBatch>> MissingResponse = Task.FromResult;

            public static Func<StateSyncBatch, Task<StateSyncBatch>> MissingRequest = request =>
            {
                request.Responses = new byte[request.StateSyncs.Length][];

                int i = 0;
                foreach (StateSyncItem _ in request.StateSyncs)
                {
                    request.Responses[i++] = null;
                }

                request.StateSyncs = null;

                return Task.FromResult(request);
            };

            public static Func<StateSyncBatch, Task<StateSyncBatch>> EmptyArraysInResponses = request =>
            {
                request.Responses = new byte[request.StateSyncs.Length][];

                int i = 0;
                foreach (StateSyncItem _ in request.StateSyncs)
                {
                    request.Responses[i++] = new byte[0];
                }

                return Task.FromResult(request);
            };

            private Func<StateSyncBatch, Task<StateSyncBatch>> _executorResultFunction = NotPreimage;

            public MaliciousExecutorMock(Func<StateSyncBatch, Task<StateSyncBatch>> executorResultFunction = null)
            {
                if (executorResultFunction != null)
                {
                    _executorResultFunction = executorResultFunction;
                }
            }

            public Task<StateSyncBatch> ExecuteRequest(CancellationToken token, StateSyncBatch batch)
            {
                return _executorResultFunction.Invoke(batch);
            }
        }

        private Address _currentAddress = Address.Zero;

        private Address NextAddress()
        {
            _currentAddress = new Address(Keccak.Compute(_currentAddress.Bytes).Bytes.Slice(12, 20));
            return _currentAddress;
        }

        public Account NextSimpleAccount()
        {
            return new Account(1);
        }

        public Account NextAccountWithCode()
        {
            byte[] byteCode = _cryptoRandom.GenerateRandomBytes(100);
            Keccak codeHash = Keccak.Compute(byteCode);
            _remoteCodeDb[codeHash.Bytes] = byteCode;

            return new Account(2, 100, Keccak.EmptyTreeHash, codeHash);
        }

//        private static ILogManager _logManager = LimboLogs.Instance;
        private ConsoleAsyncLogger _logger;
        private ILogManager _logManager;

        [Test]
        public async Task Can_download_an_empty_tree()
        {
            ExecutorMock mock = new ExecutorMock(_remoteStateDb, _remoteCodeDb);
            NodeDataDownloader downloader = new NodeDataDownloader(_localCodeDb, _localStateDb, mock, _logManager);
            await downloader.SyncNodeData(CancellationToken.None, _remoteStateTree.RootHash);

            CompareTrees("END");
        }

        private int _timeoutLength = 1000000;

        [Test, TestCaseSource("Scenarios")]
        public async Task Can_download_in_multiple_connections((string Name, Action<StateTree, StateDb, MemDb> SetupTree) testCase)
        {
            testCase.SetupTree(_remoteStateTree, _remoteStateDb, _remoteCodeDb);
            _remoteStateDb.Commit();

            ExecutorMock mock = new ExecutorMock(_remoteStateDb, _remoteCodeDb);
            mock.SetFilter(new[] {_remoteStateTree.RootHash});

            NodeDataDownloader downloader = new NodeDataDownloader(_localCodeDb, _localStateDb, mock, _logManager);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, _remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            _localStateDb.Commit();

            mock.SetFilter(null);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, _remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            _localStateDb.Commit();

            CompareTrees("END");
        }

        [Test, TestCaseSource("Scenarios")]
        public async Task Can_download_with_moving_target((string Name, Action<StateTree, StateDb, MemDb> SetupTree) testCase)
        {
            testCase.SetupTree(_remoteStateTree, _remoteStateDb, _remoteCodeDb);
            _remoteStateDb.Commit();

            ExecutorMock mock = new ExecutorMock(_remoteStateDb, _remoteCodeDb);
            mock.SetFilter(((MemDb) _remoteStateDb._db).Keys.Take(((MemDb) _remoteStateDb._db).Keys.Count - 1).Select(k => new Keccak(k)).ToArray());

            CompareTrees("BEFORE FIRST SYNC");
            
            NodeDataDownloader downloader = new NodeDataDownloader(_localCodeDb, _localStateDb, mock, _logManager);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, _remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            _localStateDb.Commit();

            CompareTrees("AFTER FIRST SYNC");
            
            _localStateTree.RootHash = _remoteStateTree.RootHash;
            _remoteStateTree.Set(TestItem.AddressA, AccountJustState0.WithChangedBalance(123.Ether()));
            _remoteStateTree.Set(TestItem.AddressB, AccountJustState1.WithChangedBalance(123.Ether()));
            _remoteStateTree.Set(TestItem.AddressC, AccountJustState2.WithChangedBalance(123.Ether()));
            
            CompareTrees("BEFORE ROOT HASH UPDATE");
            
            _remoteStateTree.UpdateRootHash();
            
            CompareTrees("BEFORE COMMIT");
            
            _remoteStateTree.Commit();
            _remoteStateDb.Commit();

            mock.SetFilter(null);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, _remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            _localStateDb.Commit();

            CompareTrees("END");
            CompareCodeDbs();
        }

        [Test, TestCaseSource("Scenarios")]
        public async Task Can_download_when_executor_sends_shorter_responses((string Name, Action<StateTree, StateDb, MemDb> SetupTree) testCase)
        {
            testCase.SetupTree(_remoteStateTree, _remoteStateDb, _remoteCodeDb);
            _remoteStateDb.Commit();
            
            CompareTrees("BEGIN");
            
            ExecutorMock mock = new ExecutorMock(_remoteStateDb, _remoteCodeDb);
            mock.MaxResponseLength = 1;

            NodeDataDownloader downloader = new NodeDataDownloader(_localCodeDb, _localStateDb, mock, _logManager);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, _remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            _localStateDb.Commit();

            CompareTrees("END");
        }

        [Test, TestCaseSource("Scenarios")]
        public async Task Can_download_a_full_state((string Name, Action<StateTree, StateDb, MemDb> SetupTree) testCase)
        {
            testCase.SetupTree(_remoteStateTree, _remoteStateDb, _remoteCodeDb);
            _remoteStateDb.Commit();

            CompareTrees("BEGIN");
            
            ExecutorMock mock = new ExecutorMock(_remoteStateDb, _remoteCodeDb);
            NodeDataDownloader downloader = new NodeDataDownloader(_localCodeDb, _localStateDb, mock, _logManager);
            Task syncNode = downloader.SyncNodeData(CancellationToken.None, _remoteStateTree.RootHash);
            
            Task first = await Task.WhenAny(syncNode, Task.Delay(_timeoutLength));
            if (first == syncNode)
            {
                if (syncNode.IsFaulted)
                {
                    throw syncNode.Exception;
                }
            }
            
            _localStateDb.Commit();

            CompareTrees("END");
        }

        [Test]
        public async Task Throws_when_peer_sends_data_that_is_not_the_preimage()
        {
            MaliciousExecutorMock mock = new MaliciousExecutorMock(MaliciousExecutorMock.NotPreimage);
            NodeDataDownloader downloader = new NodeDataDownloader(_localCodeDb, _localStateDb, mock, _logManager);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, Keccak.Compute("the_peer_has_no_data")), Task.Delay(20000000)).Unwrap()
                .ContinueWith(t =>
                {
                    Assert.True(t.IsFaulted);
                    Assert.AreEqual(typeof(AggregateException), t.Exception?.GetType());
                    Assert.AreEqual(typeof(EthSynchronizationException), t.Exception?.InnerExceptions[0].GetType());
                });
        }

        [Test]
        public async Task Throws_when_peer_sends_null_response()
        {
            MaliciousExecutorMock mock = new MaliciousExecutorMock(MaliciousExecutorMock.MissingResponse);
            NodeDataDownloader downloader = new NodeDataDownloader(_localCodeDb, _localStateDb, mock, _logManager);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, Keccak.Compute("the_peer_has_no_data")), Task.Delay(_timeoutLength)).Unwrap()
                .ContinueWith(t =>
                {
                    Assert.True(t.IsFaulted);
                    Assert.AreEqual(typeof(AggregateException), t.Exception?.GetType());
                    Assert.AreEqual(typeof(EthSynchronizationException), t.Exception?.InnerExceptions[0].GetType());
                });
        }

        [Test]
        public async Task Throws_when_peer_sends_null_request()
        {
            MaliciousExecutorMock mock = new MaliciousExecutorMock(MaliciousExecutorMock.MissingRequest);
            NodeDataDownloader downloader = new NodeDataDownloader(_localCodeDb, _localStateDb, mock, _logManager);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, Keccak.Compute("the_peer_has_no_data")), Task.Delay(_timeoutLength)).Unwrap()
                .ContinueWith(t =>
                {
                    Assert.True(t.IsFaulted);
                    Assert.AreEqual(typeof(AggregateException), t.Exception?.GetType());
                    Assert.AreEqual(typeof(EthSynchronizationException), t.Exception?.InnerExceptions[0].GetType());
                });
        }

        [Test]
        public async Task Throws_when_peer_sends_empty_byte_arrays()
        {
            MaliciousExecutorMock mock = new MaliciousExecutorMock(MaliciousExecutorMock.EmptyArraysInResponses);
            NodeDataDownloader downloader = new NodeDataDownloader(_localCodeDb, _localStateDb, mock, _logManager);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, Keccak.Compute("the_peer_has_no_data")), Task.Delay(_timeoutLength)).Unwrap()
                .ContinueWith(t =>
                {
                    Assert.True(t.IsFaulted);
                    Assert.AreEqual(typeof(AggregateException), t.Exception?.GetType());
                    Assert.AreEqual(typeof(EthSynchronizationException), t.Exception?.InnerExceptions[0].GetType());
                });
        }

        private void CompareTrees(string stage)
        {
            _logger.Info($"==================== {stage} ====================");
            _localStateTree.RootHash = _remoteStateTree.RootHash;
            
            _logger.Info($"-------------------- REMOTE --------------------");
            TreeDumper dumper = new TreeDumper();
            _remoteStateTree.Accept(dumper);
            string local = dumper.ToString();
            _logger.Info(local);
            _logger.Info($"-------------------- LOCAL --------------------");
            dumper.Reset();
            _localStateTree.Accept(dumper);
            string remote = dumper.ToString();
            _logger.Info(remote);

            if (stage == "END")
            {
                Assert.AreEqual(remote, local);
                TrieStatsCollector collector = new TrieStatsCollector();
                _localStateTree.Accept(collector);
                Assert.AreEqual(0, collector.Stats.MissingNodes.Count);
            }

//            Assert.AreEqual(_remoteCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
//            Assert.AreEqual(_remoteCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
//
//            Assert.AreEqual(_remoteDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
//            Assert.AreEqual(_remoteDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
        }
        
        private void CompareCodeDbs()
        {            
//            Assert.AreEqual(_remoteCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
//            Assert.AreEqual(_remoteCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");

//            Assert.AreEqual(_remoteDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
//            Assert.AreEqual(_remoteDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
        }
    }
}