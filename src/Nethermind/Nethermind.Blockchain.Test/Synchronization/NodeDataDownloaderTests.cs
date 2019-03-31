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
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    public class NodeDataDownloaderTests
    {
        private static readonly Account _empty = Build.An.Account.WithBalance(0).TestObject;
        private static readonly Account _account0 = Build.An.Account.WithBalance(1).TestObject;
        private static readonly Account _account1 = Build.An.Account.WithBalance(2).TestObject;
        private static readonly Account _account2 = Build.An.Account.WithBalance(3).TestObject;
        private static readonly Account _account3 = Build.An.Account.WithBalance(4).TestObject;  
        
        public static (string Name, Action<StateTree> Action)[] Scenarios = new (string, Action<StateTree>)[]
        {
            ("set_3_via_address", tree =>
            {
                tree.Set(TestItem.AddressA, _account0);
                tree.Set(TestItem.AddressB, _account0);
                tree.Set(TestItem.AddressC, _account0);
                tree.Commit();
            }),
            ("set_3_via_hash", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Commit();
            }),
            ("set_3_delete_1", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Commit();
            }),
            ("set_3_delete_2", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Commit();
            }),
            ("set_3_delete_all", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
                tree.Commit();
            }),
            ("extension_read_full_match", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"));
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("extension_read_missing", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"));
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("extension_new_branch", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), _account2);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("extension_delete_missing", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd"), null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("extenson_create_new_extension", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000"), _account2);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111"), _account3);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_new_value", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account1);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_no_change", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_delete", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_delete_missing", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Keccak("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_update_extension", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000"), _account1);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_read", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                Account account = tree.Get(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"));
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("leaf_update_missing", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                Account account = tree.Get(new Keccak("111111111111111111111111111111111111111111111111111111111ddddddd"));
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("branch_update_missing", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), _account2);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("branch_read_missing", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"));
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
            ("branch_delete_missing", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit();
            }),
        };

        private ICryptoRandom _cryptoRandom = new CryptoRandom();
        private MemDb _remoteCodeDb;
        private MemDb _localCodeDb;
        private MemDb _remoteDb;
        private MemDb _localDb;
        private StateDb _remoteStateDb;
        private StateDb _localStateDb;
        private StateTree _remoteStateTree;

        [SetUp]
        public void Setup()
        {
            _remoteDb = new MemDb();
            _localDb = new MemDb();
            _remoteStateDb = new StateDb(_remoteDb);
            _localStateDb = new StateDb(_localDb);
            _localCodeDb = new MemDb();
            _remoteCodeDb = new MemDb();

            _remoteStateTree = new StateTree(_remoteStateDb);
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

            public Task<NodeDataRequest> ExecuteRequest(NodeDataRequest request)
            {
                request.Response = new byte[request.Request.Length][];
                
                int i = 0;
                foreach ((Keccak hash, NodeDataType nodeType) in request.Request)
                {
                    request.Response[i++] = _stateDb[hash.Bytes] ?? _codeDb[hash.Bytes];
                }

                return Task.FromResult(request);
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

        [Test]
        public async Task Can_download_an_empty_tree()
        {
            ExecutorMock mock = new ExecutorMock(_remoteStateDb, _remoteCodeDb);
            NodeDataDownloader downloader = new NodeDataDownloader(_remoteStateDb, mock, LimboLogs.Instance);
            await downloader.SyncNodeData(_remoteStateTree.RootHash);

            Assert.AreEqual(_remoteCodeDb.Keys, _localCodeDb.Keys, "keys");
            Assert.AreEqual(_remoteCodeDb.Values, _localCodeDb.Values, "values");
        }

        [Test]
        public async Task Can_download_one_simple_account_tree()
        {
            ExecutorMock mock = new ExecutorMock(_remoteStateDb, _remoteCodeDb);
            NodeDataDownloader downloader = new NodeDataDownloader(_remoteStateDb, mock, LimboLogs.Instance);
            await downloader.SyncNodeData(_remoteStateTree.RootHash);

            Assert.AreEqual(_remoteCodeDb.Keys, _localCodeDb.Keys, "keys");
            Assert.AreEqual(_remoteCodeDb.Values, _localCodeDb.Values, "values");
        }

        [Test]
        public async Task Can_download_one_contract_account_tree()
        {
            ExecutorMock mock = new ExecutorMock(_remoteStateDb, _remoteCodeDb);
            NodeDataDownloader downloader = new NodeDataDownloader(_remoteStateDb, mock, LimboLogs.Instance);
            await downloader.SyncNodeData(_remoteStateTree.RootHash);

            Assert.AreEqual(_remoteCodeDb.Keys, _localCodeDb.Keys, "keys");
            Assert.AreEqual(_remoteCodeDb.Values, _localCodeDb.Values, "values");
        }
        
        [Test, TestCaseSource("Scenarios")]
        public async Task TestMethod((string Name, Action<StateTree> SetupTree) testCase)
        {
            testCase.SetupTree(_remoteStateTree);
            _remoteStateDb.Commit();
            
            ExecutorMock mock = new ExecutorMock(_remoteStateDb, _remoteCodeDb);
            NodeDataDownloader downloader = new NodeDataDownloader(_localStateDb, mock, LimboLogs.Instance);
            await downloader.SyncNodeData(_remoteStateTree.RootHash);
            _localStateDb.Commit();
            
            Assert.AreEqual(_remoteCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
            Assert.AreEqual(_remoteCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
            
            Assert.AreEqual(_remoteDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
            Assert.AreEqual(_remoteDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
        }
    }
}