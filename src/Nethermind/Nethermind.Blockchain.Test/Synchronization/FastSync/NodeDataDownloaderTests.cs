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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Store;
using Nethermind.Store.BeamSync;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Synchronization.FastSync;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization.FastSync
{
    [TestFixture, Parallelizable(ParallelScope.All)]
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

        private static (string Name, Action<StateTree, StateDb, StateDb> Action)[] _scenarios;

        public static (string Name, Action<StateTree, StateDb, StateDb> Action)[] Scenarios => LazyInitializer.EnsureInitialized(ref _scenarios, InitScenarios);

        private static (string Name, Action<StateTree, StateDb, StateDb> Action)[] InitScenarios()
        {
            return new (string, Action<StateTree, StateDb, StateDb>)[]
            {
                ("empty", (tree, stateDb, codeDb) =>
                {
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Commit();
                }),
                ("set_3_via_address", (tree, stateDb, codeDb) =>
                {
                    SetStorage(stateDb);
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(TestItem.AddressA, Account0);
                    tree.Set(TestItem.AddressB, Account0);
                    tree.Set(TestItem.AddressC, Account0);
                    tree.Commit();
                }),
                ("storage_hash_and_code_hash_same", (tree, stateDb, codeDb) =>
                {
                    byte[] code = Bytes.FromHexString("e3a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf601");
                    Keccak codeHash = Keccak.Compute(code);
                    StorageTree remoteStorageTree = new StorageTree(stateDb);
                    remoteStorageTree.Set((UInt256) 1, new byte[] {1});
                    remoteStorageTree.Commit();
                    remoteStorageTree.UpdateRootHash();
                    codeDb[codeHash.Bytes] = code;
                    tree.Set(new Keccak("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash).WithChangedCodeHash(codeHash));
                    tree.Commit();
                }),
                ("branch_with_same_accounts_at_different_addresses", (tree, stateDb, codeDb) =>
                {
                    codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                    tree.Set(new Keccak("1baaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0);
                    tree.Set(new Keccak("2baaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), AccountJustState0);
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

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void InitOnce()
        {
            if (Empty == null)
            {
                _logManager = LimboLogs.Instance;
                _logger = LimboTraceLogger.Instance;

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

        private static StorageTree SetStorage(IDb db, byte i)
        {
            StorageTree remoteStorageTree = new StorageTree(db);
            for (int j = 0; j < i; j++)
            {
                remoteStorageTree.Set((UInt256) j, new byte[] {(byte) j, (byte) i});
            }

            remoteStorageTree.Commit();
            return remoteStorageTree;
        }

        [SetUp]
        public void Setup()
        {
            InitOnce();
//            _logger = new ConsoleAsyncLogger(LogLevel.Debug);
//            _logManager = new OneLoggerLogManager(_logger);
        }

        private class DbContext
        {
            public StateDb _remoteCodeDb;
            public StateDb _localCodeDb;
            public MemDb _remoteDb;
            public MemDb _localDb;
            public StateDb _remoteStateDb;
            public StateDb _localStateDb;
            public StateTree _remoteStateTree;
            public StateTree _localStateTree;

            public DbContext()
            {
                _remoteDb = new MemDb();
                _localDb = new MemDb();
                _remoteStateDb = new StateDb(_remoteDb);
                _localStateDb = new StateDb(_localDb);
                _localCodeDb = new StateDb(_localDb);
                _remoteCodeDb = new StateDb(_remoteDb);

                _remoteStateTree = new StateTree(_remoteStateDb);
                _localStateTree = new StateTree(_localStateDb);
            }
        }

        [TearDown]
        public void TearDown()
        {
            (_logger as ConsoleAsyncLogger)?.Flush();
        }

        private static readonly IBlockTree BlockTree = Build.A.BlockTree().OfChainLength(100).TestObject;

        private class ExecutorMock : ISyncPeer
        {
            private readonly StateDb _stateDb;
            private readonly StateDb _codeDb;

            public ExecutorMock(StateDb stateDb, StateDb codeDb, Func<IList<Keccak>, Task<byte[][]>> executorResultFunction = null)
            {
                _stateDb = stateDb;
                _codeDb = codeDb;

                if (executorResultFunction != null)
                {
                    _executorResultFunction = executorResultFunction;
                }

                Node = new Node(TestItem.PublicKeyA, "127.0.0.1", 30302, true);
            }

            private Keccak[] _filter;
            public int MaxResponseLength { get; set; } = int.MaxValue;

            public void SetFilter(Keccak[] availableHashes)
            {
                _filter = availableHashes;
            }

            public static Func<IList<Keccak>, Task<byte[][]>> NotPreimage = request =>
            {
                byte[][] result = new byte[request.Count][];

                int i = 0;
                foreach (Keccak _ in request)
                {
                    result[i++] = new byte[] {1, 2, 3};
                }

                return Task.FromResult(result);
            };

            public static Func<IList<Keccak>, Task<byte[][]>> EmptyArraysInResponses = request =>
            {
                byte[][] result = new byte[request.Count][];

                int i = 0;
                foreach (Keccak _ in request)
                {
                    result[i++] = new byte[0];
                }

                return Task.FromResult(result);
            };

            private Func<IList<Keccak>, Task<byte[][]>> _executorResultFunction;

            public Guid SessionId { get; }
            public bool IsFastSyncSupported { get; }
            public Node Node { get; }
            public string ClientId { get; }
            public UInt256 TotalDifficultyOnSessionStart { get; }

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
                return Task.FromResult(BlockTree.Head);
            }

            public void SendNewBlock(Block block)
            {
                throw new NotImplementedException();
            }

            public void HintNewBlock(Keccak blockHash, long number)
            {
                throw new NotImplementedException();
            }

            public void SendNewTransaction(Transaction transaction)
            {
                throw new NotImplementedException();
            }

            public Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
            {
                if (_executorResultFunction != null)
                {
                    return _executorResultFunction(hashes);
                }

                byte[][] responses = new byte[hashes.Count][];

                int i = 0;
                foreach (Keccak item in hashes)
                {
                    if (i >= MaxResponseLength)
                    {
                        break;
                    }

                    if (_filter == null || _filter.Contains(item))
                    {
                        responses[i] = _stateDb[item.Bytes] ?? _codeDb[item.Bytes];
                    }

                    i++;
                }

                return Task.FromResult(responses);
            }
        }

        private const int _parallelism = 25;

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
            DbContext dbContext = new DbContext();
            byte[] byteCode = _cryptoRandom.GenerateRandomBytes(100);
            Keccak codeHash = Keccak.Compute(byteCode);
            dbContext._remoteCodeDb[codeHash.Bytes] = byteCode;

            return new Account(2, 100, Keccak.EmptyTreeHash, codeHash);
        }

//        private static ILogManager _logManager = LimboLogs.Instance;
        private ILogger _logger;
        private ILogManager _logManager;

        [Test]
        public async Task Can_download_an_empty_tree()
        {
            DbContext dbContext = new DbContext();
            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb);
            NodeDataDownloader downloader = PrepareDownloader(mock);
            await downloader.SyncNodeData(CancellationToken.None, 1000, dbContext._remoteStateTree.RootHash);

            CompareTrees("END");
        }

        private IEthSyncPeerPool _pool;

        private NodeDataDownloader PrepareDownloader(ISyncPeer syncPeer)
        {
            DbContext dbContext = new DbContext();
            BlockTree blockTree = Build.A.BlockTree().OfChainLength((int) BlockTree.BestSuggestedHeader.Number).TestObject;
            _pool = new EthSyncPeerPool(blockTree, new NodeStatsManager(new StatsConfig(), LimboLogs.Instance), new SyncConfig {FastSync = true}, 25, LimboLogs.Instance);
            _pool.Start();
            _pool.AddPeer(syncPeer);

            NodeDataFeed feed = new NodeDataFeed(dbContext._localCodeDb, dbContext._localStateDb, _logManager);
            NodeDataDownloader downloader = new NodeDataDownloader(_pool, feed, NullDataConsumer.Instance, _logManager);

            return downloader;
        }

        private int _timeoutLength = 1000;

        [Test, TestCaseSource("Scenarios"), Retry(5)]
        public async Task Can_download_in_multiple_connections((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext();
            testCase.SetupTree(dbContext._remoteStateTree, dbContext._remoteStateDb, dbContext._remoteCodeDb);
            dbContext._remoteStateDb.Commit();

            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb);
            mock.SetFilter(new[] {dbContext._remoteStateTree.RootHash});

            NodeDataDownloader downloader = PrepareDownloader(mock);

            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            dbContext._localStateDb.Commit();

            _pool.WakeUpAll();

            mock.SetFilter(null);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            dbContext._localStateDb.Commit();

            CompareTrees("END");
        }

        [Test, TestCaseSource("Scenarios"), Retry(5)]
        public async Task Can_download_with_moving_target((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext();
            testCase.SetupTree(dbContext._remoteStateTree, dbContext._remoteStateDb, dbContext._remoteCodeDb);
            dbContext._remoteStateDb.Commit();

            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb);
            mock.SetFilter(((MemDb) dbContext._remoteStateDb._db).Keys.Take(((MemDb) dbContext._remoteStateDb._db).Keys.Count - 1).Select(k => new Keccak(k)).ToArray());

            CompareTrees("BEFORE FIRST SYNC");

            NodeDataDownloader downloader = PrepareDownloader(mock);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            dbContext._localStateDb.Commit();

            CompareTrees("AFTER FIRST SYNC");

            dbContext._localStateTree.RootHash = dbContext._remoteStateTree.RootHash;
            dbContext._remoteStateTree.Set(TestItem.AddressA, AccountJustState0.WithChangedBalance(123.Ether()));
            dbContext._remoteStateTree.Set(TestItem.AddressB, AccountJustState1.WithChangedBalance(123.Ether()));
            dbContext._remoteStateTree.Set(TestItem.AddressC, AccountJustState2.WithChangedBalance(123.Ether()));

            CompareTrees("BEFORE ROOT HASH UPDATE");

            dbContext._remoteStateTree.UpdateRootHash();

            CompareTrees("BEFORE COMMIT");

            dbContext._remoteStateTree.Commit();
            dbContext._remoteStateDb.Commit();

            _pool.WakeUpAll();

            mock.SetFilter(null);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            dbContext._localStateDb.Commit();

            CompareTrees("END");
            CompareCodeDbs();
        }

        [Test, TestCaseSource("Scenarios"), Retry(5)]
        public async Task Big_test((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext();
            dbContext._remoteCodeDb[Keccak.Compute(Code0).Bytes] = Code0;
            dbContext._remoteCodeDb[Keccak.Compute(Code1).Bytes] = Code1;
            dbContext._remoteCodeDb[Keccak.Compute(Code2).Bytes] = Code2;
            dbContext._remoteCodeDb[Keccak.Compute(Code3).Bytes] = Code3;
            testCase.SetupTree(dbContext._remoteStateTree, dbContext._remoteStateDb, dbContext._remoteCodeDb);
            dbContext._remoteStateDb.Commit();

            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb);
            mock.SetFilter(((MemDb) dbContext._remoteStateDb._db).Keys.Take(((MemDb) dbContext._remoteStateDb._db).Keys.Count - 4).Select(k => new Keccak(k)).ToArray());

            CompareTrees("BEFORE FIRST SYNC", true);

            NodeDataDownloader downloader = PrepareDownloader(mock);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            dbContext._localStateDb.Commit();

            CompareTrees("AFTER FIRST SYNC", true);

            dbContext._localStateTree.RootHash = dbContext._remoteStateTree.RootHash;
            for (byte i = 0; i < 8; i++)
            {
                dbContext._remoteStateTree
                    .Set(TestItem.Addresses[i], AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(1)
                        .WithChangedCodeHash(Keccak.Compute(Code3))
                        .WithChangedStorageRoot(SetStorage(dbContext._remoteStateDb, i).RootHash));
            }

            dbContext._remoteStateTree.UpdateRootHash();
            dbContext._remoteStateTree.Commit();
            dbContext._remoteStateDb.Commit();

            _pool.WakeUpAll();
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            dbContext._localStateDb.Commit();

            CompareTrees("AFTER SECOND SYNC", true);

            dbContext._localStateTree.RootHash = dbContext._remoteStateTree.RootHash;
            for (byte i = 0; i < 16; i++)
            {
                dbContext._remoteStateTree
                    .Set(TestItem.Addresses[i], AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(2)
                        .WithChangedCodeHash(Keccak.Compute(Code3))
                        .WithChangedStorageRoot(SetStorage(dbContext._remoteStateDb, (byte) (i % 7)).RootHash));
            }

            dbContext._remoteStateTree.UpdateRootHash();
            dbContext._remoteStateTree.Commit();
            dbContext._remoteStateDb.Commit();

            _pool.WakeUpAll();
            mock.SetFilter(null);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            dbContext._localStateDb.Commit();

            CompareTrees("END");
            CompareCodeDbs();
        }

        [Test, TestCaseSource("Scenarios"), Retry(5)]
        public async Task Can_download_when_executor_sends_shorter_responses((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext();
            testCase.SetupTree(dbContext._remoteStateTree, dbContext._remoteStateDb, dbContext._remoteCodeDb);
            dbContext._remoteStateDb.Commit();

            CompareTrees("BEGIN");

            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb);
            mock.MaxResponseLength = 1;

            NodeDataDownloader downloader = PrepareDownloader(mock);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash), Task.Delay(_timeoutLength));
            dbContext._localStateDb.Commit();

            CompareTrees("END");
        }

        [Test, TestCaseSource("Scenarios"), Retry(5)]
        public async Task Can_download_a_full_state((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext();
            testCase.SetupTree(dbContext._remoteStateTree, dbContext._remoteStateDb, dbContext._remoteCodeDb);
            dbContext._remoteStateDb.Commit();

            CompareTrees("BEGIN");

            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb);
            NodeDataDownloader downloader = PrepareDownloader(mock);
            Task syncNode = downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash);

            Task first = await Task.WhenAny(syncNode, Task.Delay(_timeoutLength));
            if (first == syncNode)
            {
                if (syncNode.IsFaulted)
                {
                    throw syncNode.Exception;
                }
            }

            dbContext._localStateDb.Commit();
            CompareTrees("END");
        }

        [Test, TestCaseSource("Scenarios"), Retry(5)]
        public async Task Dependent_branch_counter_is_zero_and_leaf_is_short((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext();
            testCase.SetupTree(dbContext._remoteStateTree, dbContext._remoteStateDb, dbContext._remoteCodeDb);
            dbContext._remoteStateDb.Commit();

            StorageTree remoteStorageTree = new StorageTree(dbContext._remoteDb);
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb000"), new byte[] {1});
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb111"), new byte[] {1});
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeb000000000000000000000000000000000000000000"), new byte[] {1});
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeb111111111111111111111111111111111111111111"), new byte[] {1});
            remoteStorageTree.Commit();

            dbContext._remoteStateTree.Set(TestItem.AddressD, AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext._remoteStateTree.Commit();

            CompareTrees("BEGIN");

            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb);
            NodeDataDownloader downloader = PrepareDownloader(mock);
            Task syncNode = downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash);

            Task first = await Task.WhenAny(syncNode, Task.Delay(_timeoutLength));
            if (first == syncNode)
            {
                if (syncNode.IsFaulted)
                {
                    throw syncNode.Exception;
                }
            }

            dbContext._localStateDb.Commit();

            CompareTrees("END");
        }

        [Test, TestCaseSource("Scenarios"), Retry(5)]
        public async Task Scenario_plus_one_storage((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext();
            testCase.SetupTree(dbContext._remoteStateTree, dbContext._remoteStateDb, dbContext._remoteCodeDb);
            dbContext._remoteStateDb.Commit();

            StorageTree remoteStorageTree = new StorageTree(dbContext._remoteDb);
            remoteStorageTree.Set((UInt256) 1, new byte[] {1});
            remoteStorageTree.Commit();

            dbContext._remoteStateTree.Set(TestItem.AddressD, AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext._remoteStateTree.Commit();

            CompareTrees("BEGIN");

            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb);
            NodeDataDownloader downloader = PrepareDownloader(mock);
            Task syncNode = downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash);

            Task first = await Task.WhenAny(syncNode, Task.Delay(_timeoutLength));
            if (first == syncNode)
            {
                if (syncNode.IsFaulted)
                {
                    throw syncNode.Exception;
                }
            }

            dbContext._localStateDb.Commit();

            CompareTrees("END");
        }

        [Test, TestCaseSource("Scenarios"), Retry(5)]
        public async Task Scenario_plus_one_code((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext();
            testCase.SetupTree(dbContext._remoteStateTree, dbContext._remoteStateDb, dbContext._remoteCodeDb);
            dbContext._remoteStateDb.Commit();

            dbContext._remoteCodeDb.Set(Keccak.Compute(Code0), Code0);
            dbContext._remoteCodeDb.Commit();

            dbContext._remoteStateTree.Set(TestItem.AddressD, AccountJustState0.WithChangedCodeHash(Keccak.Compute(Code0)));
            dbContext._remoteStateTree.Commit();

            CompareTrees("BEGIN");

            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb);
            NodeDataDownloader downloader = PrepareDownloader(mock);
            Task syncNode = downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash);

            Task first = await Task.WhenAny(syncNode, Task.Delay(_timeoutLength));
            if (first == syncNode)
            {
                if (syncNode.IsFaulted)
                {
                    throw syncNode.Exception;
                }
            }

            dbContext._localStateDb.Commit();

            CompareTrees("END");
        }

        [Test, TestCaseSource("Scenarios"), Retry(5)]
        public async Task Scenario_plus_one_code_one_storage((string Name, Action<StateTree, StateDb, StateDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext();
            testCase.SetupTree(dbContext._remoteStateTree, dbContext._remoteStateDb, dbContext._remoteCodeDb);
            dbContext._remoteStateDb.Commit();

            dbContext._remoteCodeDb.Set(Keccak.Compute(Code0), Code0);
            dbContext._remoteCodeDb.Commit();

            StorageTree remoteStorageTree = new StorageTree(dbContext._remoteDb);
            remoteStorageTree.Set((UInt256) 1, new byte[] {1});
            remoteStorageTree.Commit();

            dbContext._remoteStateTree.Set(TestItem.AddressD, AccountJustState0.WithChangedCodeHash(Keccak.Compute(Code0)).WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext._remoteStateTree.Commit();

            CompareTrees("BEGIN");

            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb);
            NodeDataDownloader downloader = PrepareDownloader(mock);
            Task syncNode = downloader.SyncNodeData(CancellationToken.None, 1024, dbContext._remoteStateTree.RootHash);

            Task first = await Task.WhenAny(syncNode, Task.Delay(_timeoutLength));
            if (first == syncNode)
            {
                if (syncNode.IsFaulted)
                {
                    throw syncNode.Exception;
                }
            }

            dbContext._localStateDb.Commit();

            CompareTrees("END");
        }

        [Test]
        public async Task Silences_bad_peers()
        {
            DbContext dbContext = new DbContext();
            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb, ExecutorMock.NotPreimage);
            NodeDataDownloader downloader = PrepareDownloader(mock);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, 1024, Keccak.Compute("the_peer_has_no_data")), Task.Delay(1000)).Unwrap()
                .ContinueWith(t => { Assert.AreEqual(0, _pool.UsefulPeerCount); });
        }

        [Test]
        [Retry(5)]
        public async Task Silences_when_peer_sends_empty_byte_arrays()
        {
            DbContext dbContext = new DbContext();
            ExecutorMock mock = new ExecutorMock(dbContext._remoteStateDb, dbContext._remoteCodeDb, ExecutorMock.EmptyArraysInResponses);
            NodeDataDownloader downloader = PrepareDownloader(mock);
            await Task.WhenAny(downloader.SyncNodeData(CancellationToken.None, 1024, Keccak.Compute("the_peer_has_no_data")), Task.Delay(1000)).Unwrap()
                .ContinueWith(t => { Assert.AreEqual(0, _pool.UsefulPeerCount); });
        }

        private void CompareTrees(string stage, bool skipLogs = false)
        {
            DbContext dbContext = new DbContext();
            if (!skipLogs) _logger.Info($"==================== {stage} ====================");
            dbContext._localStateTree.RootHash = dbContext._remoteStateTree.RootHash;

            if (!skipLogs) _logger.Info($"-------------------- REMOTE --------------------");
            TreeDumper dumper = new TreeDumper();
            dbContext._remoteStateTree.Accept(dumper, dbContext._remoteStateTree.RootHash);
            string remote = dumper.ToString();
            if (!skipLogs) _logger.Info(remote);
            if (!skipLogs) _logger.Info($"-------------------- LOCAL --------------------");
            dumper.Reset();
            dbContext._localStateTree.Accept(dumper, dbContext._localStateTree.RootHash);
            string local = dumper.ToString();
            if (!skipLogs) _logger.Info(local);

            if (stage == "END")
            {
                Assert.AreEqual(remote, local, $"{remote}{Environment.NewLine}{local}");
                TrieStatsCollector collector = new TrieStatsCollector(dbContext._localCodeDb, _logManager);
                dbContext._localStateTree.Accept(collector, dbContext._localStateTree.RootHash);
                Assert.AreEqual(0, collector.Stats.MissingCode);
            }

//            Assert.AreEqual(dbContext._remoteCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
//            Assert.AreEqual(dbContext._remoteCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
//
//            Assert.AreEqual(dbContext._remoteDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
//            Assert.AreEqual(dbContext._remoteDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
        }

        private void CompareCodeDbs()
        {
//            Assert.AreEqual(dbContext._remoteCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
//            Assert.AreEqual(dbContext._remoteCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");

//            Assert.AreEqual(dbContext._remoteDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
//            Assert.AreEqual(dbContext._remoteDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
        }
    }
}