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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.BeamSync
{
    [TestFixture]
    public class BeamSyncDbTests
    {
        private BeamSyncDb _stateBeamLocal;
        private BeamSyncDb _codeBeamLocal;
        private ISnapshotableDb _stateLocal;
        private ISnapshotableDb _codeLocal;
        private StateReader _stateReader;
        private int _needMoreDataInvocations;
        private StateTree _remoteStateTrie;
        private StateDb _remoteState;
        private StateDb _remoteCode;
        public static (string Name, Action<StateTree, StateDb, StateDb> Action)[] Scenarios => TrieScenarios.Scenarios;

        [SetUp]
        public void Setup()
        {
            _needMoreDataInvocations = 0;
            BeamSyncContext.LoopIterationsToFailInTest.Value = null;
            MakeRequestsNeverExpire();
        }

        private void MakeRequestsNeverExpire()
        {
            BeamSyncContext.LastFetchUtc.Value = DateTime.MaxValue;
        }
        
        private void MakeRequestsImmediatelyExpire()
        {
            BeamSyncContext.LastFetchUtc.Value = DateTime.MinValue;
        }

        [Test]
        public void Beam_db_provider_smoke_test()
        {
            BeamSyncDbProvider dbProvider = new BeamSyncDbProvider(new MemDbProvider(), "description", LimboLogs.Instance);
            // has to be state DB on the outside
            Assert.IsInstanceOf(typeof(StateDb), dbProvider.StateDb);
            Assert.IsInstanceOf(typeof(StateDb), dbProvider.CodeDb);
        }

        [Test]
        public void Beam_db_provider_can_dispose()
        {
            BeamSyncDbProvider dbProvider = new BeamSyncDbProvider(new MemDbProvider(), "description", LimboLogs.Instance);
            dbProvider.Dispose();
        }

        [TestCase("leaf_read")]
        public void Propagates_exception(string name)
        {
            (string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);
            BeamSyncContext.LoopIterationsToFailInTest.Value = 4;
            Assert.Throws<Exception>(() =>
                _stateReader.GetAccount(_remoteStateTrie.RootHash, TestItem.AddressA));
        }

        [TestCase("leaf_read")]
        public void Invokes_need_more_data_in_each_loop(string name)
        {
            (string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);
            RunRounds(4);

            Assert.AreEqual(4, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public void Does_ask_for_what_has_already_been_sent(string name)
        {
            (string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            RunRounds(1);
            _stateBeamLocal.PrepareRequests();
            RunRounds(3);

            Assert.AreEqual(4, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public void Empty_response_brings_it_back_in_the_loop(string name)
        {
            (string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            RunRounds(1);
            DataConsumerRequest[] request = _stateBeamLocal.PrepareRequests();
            _stateBeamLocal.HandleResponse(request[0], new byte[request[0].Keys.Length][]);
            RunRounds(3);

            Assert.AreEqual(4, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public void Can_prepare_empty_request(string name)
        {
            (string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            DataConsumerRequest[] request = _stateBeamLocal.PrepareRequests();
            Assert.AreEqual(0, request.Length);
        }

        [TestCase("leaf_read")]
        public void Full_response_works(string name)
        {
            (string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            RunRounds(1);
            DataConsumerRequest[] request = _stateBeamLocal.PrepareRequests();
            _stateBeamLocal.HandleResponse(request[0], new byte[][] {_remoteState.Get(request[0].Keys[0])});
            RunRounds(3);

            Assert.AreEqual(1, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public void Full_response_stops_it(string name)
        {
            (string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            Task.Run(() => RunRounds(100));
            DataConsumerRequest[] request = new DataConsumerRequest[0];
            for (int i = 0; i < 1000; i++)
            {
                Thread.Sleep(1);
                request = _stateBeamLocal.PrepareRequests();
                if (request.Length > 0)
                {
                    break;
                }
            }

            _stateBeamLocal.HandleResponse(request[0], new byte[][] {_remoteState.Get(request[0].Keys[0])});

            Assert.Less(_needMoreDataInvocations, 1000);
        }

        [TestCase("leaf_read")]
        public void Can_resolve_from_local(string name)
        {
            (string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            RunRounds(1);
            DataConsumerRequest[] request = _stateBeamLocal.PrepareRequests();
            _stateBeamLocal.HandleResponse(request[0], new byte[][] {_remoteState.Get(request[0].Keys[0])});
            PatriciaTree.NodeCache.Clear();
            RunRounds(1);

            Assert.AreEqual(1, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public void Expires_when_much_in_the_past(string name)
        {
            (string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow.AddSeconds(-100);
            RunRounds(10);
            Assert.AreEqual(0, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public void Invalid_response_brings_it_back(string name)
        {
            (string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            RunRounds(1);
            DataConsumerRequest[] request = _stateBeamLocal.PrepareRequests();
            _stateBeamLocal.HandleResponse(request[0], new byte[][] {new byte[] {1, 2, 3}});
            RunRounds(3);

            Assert.AreEqual(4, _needMoreDataInvocations);
        }

        private void RunRounds(int rounds)
        {
            try
            {
                BeamSyncContext.LoopIterationsToFailInTest.Value = rounds;
                _stateReader.GetAccount(_remoteStateTrie.RootHash, TestItem.AddressA);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private void Setup((string Name, Action<StateTree, StateDb, StateDb> SetupTree) scenario)
        {
            TrieScenarios.InitOnce();
            _remoteState = new StateDb(new MemDb());
            _remoteCode = new StateDb(new MemDb());
            _remoteStateTrie = new StateTree(_remoteState);
            scenario.SetupTree(_remoteStateTrie, _remoteState, _remoteCode);
            _remoteStateTrie.UpdateRootHash();

            _stateBeamLocal = new BeamSyncDb(new MemDb(), LimboLogs.Instance);
            _codeBeamLocal = new BeamSyncDb(new MemDb(), LimboLogs.Instance);
            _stateLocal = new StateDb(_stateBeamLocal);
            _codeLocal = new StateDb(_codeBeamLocal);

            _stateReader = new StateReader(_stateLocal, _codeLocal, LimboLogs.Instance);
            _stateBeamLocal.NeedMoreData += (sender, args) => { Interlocked.Increment(ref _needMoreDataInvocations); };
            PatriciaTree.NodeCache.Clear();
        }

        [Test]
        public void Saves_nodes_to_beam_temp_db()
        {
            MemDb tempDb = new MemDb();
            MemDb stateDB = new MemDb();
            BeamSyncDb beamSyncDb = new BeamSyncDb(stateDB, tempDb, LimboLogs.Instance);

            byte[] bytes = new byte[] {1, 2, 3};
            beamSyncDb.Set(TestItem.KeccakA, bytes);
            byte[] retrievedFromTemp = tempDb.Get(TestItem.KeccakA);
            retrievedFromTemp.Should().BeEquivalentTo(bytes);
        }
        
        [Test]
        public void Does_not_save_nodes_to_state_db()
        {
            MemDb tempDb = new MemDb();
            MemDb stateDB = new MemDb();
            BeamSyncDb beamSyncDb = new BeamSyncDb(stateDB, tempDb, LimboLogs.Instance);

            byte[] bytes = new byte[] {1, 2, 3};
            beamSyncDb.Set(TestItem.KeccakA, bytes);
            byte[] retrievedFromTemp = stateDB.Get(TestItem.KeccakA);
            retrievedFromTemp.Should().BeNull();
        }
        
        [Test]
        public void Can_read_nodes_from_temp_when_missing_in_state()
        {
            MemDb tempDb = new MemDb();
            MemDb stateDB = new MemDb();
            BeamSyncDb beamSyncDb = new BeamSyncDb(stateDB, tempDb, LimboLogs.Instance);

            byte[] bytes = new byte[] {1, 2, 3};
            tempDb.Set(TestItem.KeccakA, bytes);
            
            byte[] retrievedFromTemp = beamSyncDb.Get(TestItem.KeccakA);
            retrievedFromTemp.Should().BeEquivalentTo(bytes);
        }
        
        [Test]
        public void Can_read_nodes_from_state_when_missing_in_temp()
        {
            MemDb tempDb = new MemDb();
            MemDb stateDB = new MemDb();
            BeamSyncDb beamSyncDb = new BeamSyncDb(stateDB, tempDb, LimboLogs.Instance);

            byte[] bytes = new byte[] {1, 2, 3};
            stateDB.Set(TestItem.KeccakA, bytes);
            
            byte[] retrievedFromTemp = beamSyncDb.Get(TestItem.KeccakA);
            retrievedFromTemp.Should().BeEquivalentTo(bytes);
        }
    }
}