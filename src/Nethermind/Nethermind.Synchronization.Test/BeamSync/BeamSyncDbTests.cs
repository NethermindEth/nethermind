//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.BeamSync
{
    [TestFixture]
    [Parallelizable(ParallelScope.None)]
    public class BeamSyncDbTests
    {
        private BeamSyncDb _stateBeamLocal;
        private BeamSyncDb _codeBeamLocal;
        private StateReader _stateReader;
        private int _needMoreDataInvocations;
        private StateTree _remoteStateTrie;
        private IDb _remoteState;
        private IDb _remoteCode;
        public static (string Name, Action<StateTree, ITrieStore, IDb> Action)[] Scenarios => TrieScenarios.Scenarios;

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

        [Test]
        public async Task Beam_db_provider_smoke_test()
        {
            var memDbProvider = await TestMemDbProvider.InitAsync();
            IDbProvider dbProvider = new BeamSyncDbProvider(StaticSelector.Beam, memDbProvider, new SyncConfig(), LimboLogs.Instance);
            Assert.IsInstanceOf(typeof(BeamSyncDb), dbProvider.StateDb);
            Assert.IsInstanceOf(typeof(BeamSyncDb), dbProvider.CodeDb);
        }

        [Test]
        public async Task Beam_db_provider_can_dispose()
        {
            var memDbProvider = await TestMemDbProvider.InitAsync();
            BeamSyncDbProvider dbProvider = new BeamSyncDbProvider(StaticSelector.Beam, memDbProvider, new SyncConfig(), LimboLogs.Instance);
            dbProvider.Dispose();
        }

        [TestCase("leaf_read")]
        public void Propagates_exception(string name)
        {
            (string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);
            BeamSyncContext.LoopIterationsToFailInTest.Value = 4;
            Assert.Throws<Exception>(() =>
                _stateReader.GetAccount(_remoteStateTrie.RootHash, TestItem.AddressA));
        }

        [TestCase("leaf_read")]
        public void Invokes_need_more_data_in_each_loop(string name)
        {
            (string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);
            RunRounds(4);

            Assert.AreEqual(4, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public void Does_ask_for_what_has_already_been_sent(string name)
        {
            (string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            RunRounds(1);
            _stateBeamLocal.PrepareRequest();
            RunRounds(3);

            Assert.AreEqual(4, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public async Task Empty_response_brings_it_back_in_the_loop(string name)
        {
            (string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            RunRounds(1);
            StateSyncBatch request = await _stateBeamLocal.PrepareRequest();
            request!.Responses = new byte[request.RequestedNodes.Length][];
            _stateBeamLocal.HandleResponse(request);
            RunRounds(3);

            Assert.AreEqual(4, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public async Task Can_prepare_empty_request(string name)
        {
            (string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            StateSyncBatch request = await _stateBeamLocal.PrepareRequest();
            request.Should().BeNull();
        }

        [TestCase("leaf_read")]
        public async Task Full_response_works(string name)
        {
            (string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            RunRounds(1);
            StateSyncBatch request = await _stateBeamLocal.PrepareRequest();
            request!.Responses = new[] {_remoteState.Get(request.RequestedNodes[0].Hash)};
            _stateBeamLocal.HandleResponse(request);
            RunRounds(3);

            Assert.AreEqual(1, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public async Task Full_response_stops_it(string name)
        {
            (string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

#pragma warning disable 4014
            Task.Run(() => RunRounds(100));
#pragma warning restore 4014
            StateSyncBatch request = null;
            for (int i = 0; i < 1000; i++)
            {
                await Task.Delay(1);
                request = await _stateBeamLocal.PrepareRequest();
                if (request?.RequestedNodes?.Length > 0)
                {
                    break;
                }
            }

            request!.Responses = new[] {_remoteState.Get(request.RequestedNodes[0].Hash)};
            _stateBeamLocal.HandleResponse(request);

            Assert.Less(_needMoreDataInvocations, 1000);
        }

        [TestCase("leaf_read")]
        public async Task Can_resolve_from_local(string name)
        {
            (string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            RunRounds(1);
            StateSyncBatch request = await _stateBeamLocal.PrepareRequest();
            request!.Responses = new[] {_remoteState.Get(request.RequestedNodes[0].Hash)};
            _stateBeamLocal.HandleResponse(request);
            RunRounds(1);

            Assert.AreEqual(1, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public void Expires_when_much_in_the_past(string name)
        {
            (string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow.AddSeconds(-100);
            RunRounds(10);
            Assert.AreEqual(0, _needMoreDataInvocations);
        }

        [TestCase("leaf_read")]
        public async Task Invalid_response_brings_it_back(string name)
        {
            (string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario = TrieScenarios.Scenarios.SingleOrDefault(s => s.Name == name);
            Setup(scenario);

            RunRounds(1);
            StateSyncBatch request = await _stateBeamLocal.PrepareRequest();
            request!.Responses = new[] {new byte[] {1, 2, 3}};
            _stateBeamLocal.HandleResponse(request);
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

        private void Setup((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) scenario)
        {
            TrieScenarios.InitOnce();
            _remoteState = new MemDb();
            TrieStore remoteTrieStore = new TrieStore(_remoteState.Innermost, LimboLogs.Instance);
            _remoteCode = new MemDb();
            _remoteStateTrie = new StateTree(remoteTrieStore, LimboLogs.Instance);
            scenario.SetupTree(_remoteStateTrie, remoteTrieStore, _remoteCode);
            _remoteStateTrie.UpdateRootHash();

            MemDb beamStateDb = new MemDb();
            _stateBeamLocal = new BeamSyncDb(new MemDb(), beamStateDb, StaticSelector.Beam, LimboLogs.Instance);
            _codeBeamLocal = new BeamSyncDb(new MemDb(), beamStateDb, StaticSelector.Beam, LimboLogs.Instance);

            _stateReader = new StateReader(new TrieStore(_stateBeamLocal, LimboLogs.Instance), _codeBeamLocal.Innermost, LimboLogs.Instance);
            _stateBeamLocal.StateChanged += (sender, args) =>
            {
                if (_stateBeamLocal.CurrentState == SyncFeedState.Active)
                {
                    Interlocked.Increment(ref _needMoreDataInvocations);
                }
            };
        }

        [Test]
        public void Saves_nodes_to_beam_temp_db()
        {
            MemDb tempDb = new MemDb();
            MemDb stateDB = new MemDb();
            BeamSyncDb beamSyncDb = new BeamSyncDb(stateDB, tempDb, StaticSelector.Beam, LimboLogs.Instance);

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
            BeamSyncDb beamSyncDb = new BeamSyncDb(stateDB, tempDb, StaticSelector.Beam, LimboLogs.Instance);

            byte[] bytes = new byte[] {1, 2, 3};
            beamSyncDb.Set(Keccak.Compute(bytes), bytes);
            byte[] retrievedFromTemp = stateDB.Get(Keccak.Compute(bytes));
            retrievedFromTemp.Should().BeNull();
        }

        [Test]
        public void Can_read_nodes_from_temp_when_missing_in_state()
        {
            MemDb tempDb = new MemDb();
            MemDb stateDB = new MemDb();
            BeamSyncDb beamSyncDb = new BeamSyncDb(stateDB, tempDb, StaticSelector.Beam, LimboLogs.Instance);

            byte[] bytes = new byte[] {1, 2, 3};
            tempDb.Set(Keccak.Compute(bytes), bytes);

            byte[] retrievedFromTemp = beamSyncDb.Get(Keccak.Compute(bytes));
            retrievedFromTemp.Should().BeEquivalentTo(bytes);
        }

        [Test]
        public void Can_read_nodes_from_state_when_missing_in_temp()
        {
            MemDb tempDb = new MemDb();
            MemDb stateDB = new MemDb();
            BeamSyncDb beamSyncDb = new BeamSyncDb(stateDB, tempDb, StaticSelector.Beam, LimboLogs.Instance);

            byte[] bytes = new byte[] {1, 2, 3};
            stateDB.Set(Keccak.Compute(bytes), bytes);

            byte[] retrievedFromTemp = beamSyncDb.Get(Keccak.Compute(bytes));
            retrievedFromTemp.Should().BeEquivalentTo(bytes);
        }

        [Test, Description("Write through means that the beam sync DB does no longer behave like a beam sync" +
                           "and allows writes directly to the state database instead of memory. In case of" +
                           "peers disconnecting the mode changes to None and we may still be processing some previous" +
                           "blocks and we may end up saving a state root which would totally derail the state" +
                           "sync because and sync progress resolver.")]
        public void When_mode_changes_to_none_and_we_are_still_processing_a_previous_block_we_should_not_write_through()
        {
            MemDb beamDb = new MemDb();
            MemDb stateDB = new MemDb();
            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            BeamSyncDb beamSyncDb = new BeamSyncDb(stateDB, beamDb, syncModeSelector, LimboLogs.Instance);
            syncModeSelector.Current.Returns(SyncMode.Beam);
            beamSyncDb[TestItem.KeccakA.Bytes] = new byte[] {1};
            syncModeSelector.Current.Returns(SyncMode.None);
            beamSyncDb[TestItem.KeccakB.Bytes] = new byte[] {1, 2};

            stateDB[TestItem.KeccakA.Bytes].Should().BeNull();
            stateDB[TestItem.KeccakB.Bytes].Should().BeNull();

            beamDb[TestItem.KeccakA.Bytes].Should().NotBeNull();
            beamDb[TestItem.KeccakB.Bytes].Should().NotBeNull();
        }
    }
}
 
