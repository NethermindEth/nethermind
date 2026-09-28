// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test;

/// <summary>Starting the driver on a Gloas checkpoint, fresh or resumed, and refusing a resumed anchor whose state and block disagree on the fork.</summary>
public class BeaconChainServiceGloasAnchorTests
{
    /// <summary>
    /// A Gloas checkpoint is handed to the orchestrator like a Fulu one, after the pubkey cache is built from its
    /// validators. The orchestrator here has no P2P, so reaching it shows as its own refusal to run.
    /// </summary>
    [Test]
    public async Task Start_hands_a_gloas_anchor_to_the_orchestrator_both_fresh_and_resumed()
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(first.PostState, new ForkedSignedBeaconBlock.OfGloas(first.Block));
        BeaconChainConfig config = new() { CheckpointStateFile = files.StateFile, CheckpointSyncUrl = "http://invalid.localhost:1" };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);

        (TestErrorLogManager.Error[] fresh, int freshPubkeys) = await StartAsync(config, store);
        bool anchored = store.TryGetAnchor(out Hash256? anchorRoot, out _);
        (TestErrorLogManager.Error[] resumed, int resumedPubkeys) = await StartAsync(config, store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchored, Is.True, "the fresh start checkpoint-synced the Gloas anchor, so the second start resumes");
            Assert.That(anchorRoot, Is.EqualTo(first.Root));
            foreach ((TestErrorLogManager.Error[] errors, int pubkeys, string start) in new[] { (fresh, freshPubkeys, "fresh"), (resumed, resumedPubkeys, "resumed") })
            {
                Assert.That(errors, Has.Length.EqualTo(1), start);
                Assert.That(errors[0].Exception, Is.TypeOf<InvalidOperationException>().And.Message.Contains("P2P components"), $"{start}: the orchestrator was reached");
                Assert.That(pubkeys, Is.EqualTo(first.PostState.Validators!.Length), $"{start}: the pubkey cache holds the Gloas state's validators");
            }
        }
    }

    /// <summary>
    /// A resumed database whose anchor state and block are of different forks cannot seed the importer. The refusal
    /// names the block by its own slot, which differs from the state's, so an operator is not sent after the wrong block.
    /// </summary>
    [Test]
    public async Task Start_refuses_a_resumed_anchor_whose_state_and_block_are_of_different_forks([Values] bool gloasBlock)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        ulong blockSlot = gloasBlock ? chain.First.Block.Message!.Slot : chain.AnchorBlock.Slot;
        ulong stateSlot = gloasBlock ? chain.AnchorState.Slot : chain.First.PostState.Slot;
        store.PutState(chain.AnchorRoot, gloasBlock ? BeaconStateFulu.Encode(chain.AnchorState) : BeaconStateGloas.Encode(chain.First.PostState));
        store.PutForkedBlock(chain.AnchorRoot, gloasBlock
            ? new ForkedSignedBeaconBlock.OfGloas(chain.First.Block)
            : new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain.AnchorBlock, Signature = default }));
        store.SetAnchor(chain.AnchorRoot, blockSlot);

        (TestErrorLogManager.Error[] errors, int pubkeys) = await StartAsync(new BeaconChainConfig { CheckpointSyncUrl = "http://invalid.localhost:1" }, store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockSlot, Is.Not.EqualTo(stateSlot), "fixture: the block slot must be told apart from the state slot");
            Assert.That(errors, Has.Length.EqualTo(1));
            Assert.That(errors.Single().Exception, Is.Null, "a named refusal, not a crash");
            Assert.That(errors.Single().Text, Does.Contain($"at slot {blockSlot} is a {(gloasBlock ? BeaconFork.Gloas : BeaconFork.Fulu)} block"));
            Assert.That(pubkeys, Is.Zero, "refused before the pubkey cache is built");
        }
    }

    private static async Task<(TestErrorLogManager.Error[] Errors, int PubkeyCount)> StartAsync(BeaconChainConfig config, BeaconChainStore store)
    {
        BeaconChainSpec spec = GloasCheckpointFiles.Spec;
        TestErrorLogManager logManager = new();
        PubkeyCache pubkeyCache = new();
        SlotClock clock = new(spec, Timestamper.Default);
        IBeaconSyncPeerPool pool = Substitute.For<IBeaconSyncPeerPool>();
        IEngineDriver engine = Substitute.For<IEngineDriver>();
        BeaconSyncOrchestrator orchestrator = new(
            config,
            spec,
            store,
            new BlockImporterFactory(spec, store, pubkeyCache, engine, config, logManager, new DataColumnSidecarPool(), clock),
            engine,
            pool,
            new RangeSync(pool, logManager, new DataColumnSidecarPool(), spec, RangeSyncTests.ClockAtGenesis(spec)),
            clock,
            new GossipRouter(spec, clock, logManager),
            new BeaconChainStatusHolder(spec, Timestamper.Default),
            logManager);
        using CheckpointSync checkpointSync = new(config, spec, store, logManager);
        using BeaconChainService service = new(config, spec, store, pubkeyCache, checkpointSync, orchestrator, new ExternalClDetector(config, new Lazy<IEngineRpcModule>(() => Substitute.For<IEngineRpcModule>()), logManager), logManager);
        await service.Start();
        return ([.. logManager.Errors], pubkeyCache.Count);
    }
}
