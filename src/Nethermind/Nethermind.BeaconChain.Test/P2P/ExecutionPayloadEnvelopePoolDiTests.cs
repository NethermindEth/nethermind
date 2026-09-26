// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Autofac;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P;

// The pool's and the router's store and status are optional, so a registration that leaves them unfilled still resolves and checks nothing against the chain.
public class ExecutionPayloadEnvelopePoolDiTests
{
    [Test]
    public void Pool_from_the_module_reads_the_store_and_status_the_module_registers()
    {
        using IContainer container = BeaconChainTestContainer.Builder(BlockchainIds.Sepolia).Build();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        BeaconChainStatusHolder status = container.Resolve<BeaconChainStatusHolder>();
        ExecutionPayloadEnvelopePool pool = container.Resolve<ExecutionPayloadEnvelopePool>();

        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(FirstGloasSlot);
        Hash256 root = SszRoots.HashTreeRoot(block.Message!);
        store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(block));
        SignedBeaconBlockGloas child = CreateMinimalGloasBlock(FirstGloasSlot + 1, root);
        child.Message!.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockHash = block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlockHash;
        Hash256 childRoot = SszRoots.HashTreeRoot(child.Message);
        store.PutForkedBlock(childRoot, new ForkedSignedBeaconBlock.OfGloas(child));
        status.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = ForkDigest.Compute(Sepolia, Sepolia.GloasForkEpoch),
            FinalizedRoot = Hash256.Zero,
            HeadRoot = childRoot,
            HeadSlot = FirstGloasSlot + 1,
        };
        pool.Add(root, new SignedExecutionPayloadEnvelope
        {
            Message = new ExecutionPayloadEnvelope
            {
                Payload = new ExecutionPayloadGloas { SlotNumber = FirstGloasSlot },
                ExecutionRequests = new ExecutionRequestsGloas(),
                BeaconBlockRoot = root,
                ParentBeaconBlockRoot = Hash256.Zero,
            },
            Signature = new BlsSignature(new byte[BlsSignature.Length]),
        });

        Assert.That(pool.GetCanonical(FirstGloasSlot, 1).Select(static e => e.Message!.BeaconBlockRoot), Is.EqualTo(new[] { root }));
    }

    [Test]
    public void Router_from_the_module_checks_envelopes_against_the_store_the_module_registers()
    {
        using IContainer container = BeaconChainTestContainer.Builder(BlockchainIds.Sepolia).Build();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        GossipRouter router = container.Resolve<GossipRouter>();
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(FirstGloasSlot + 1);
        Hash256 root = SszRoots.HashTreeRoot(block.Message!);
        store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(block));
        SignedExecutionPayloadEnvelope envelope = new()
        {
            Message = new ExecutionPayloadEnvelope
            {
                Payload = new ExecutionPayloadGloas { SlotNumber = FirstGloasSlot + 1, BlockHash = Keccak.Compute("not the bid's"), Withdrawals = [] },
                ExecutionRequests = new ExecutionRequestsGloas(),
                BeaconBlockRoot = root,
                ParentBeaconBlockRoot = Hash256.Zero,
            },
            Signature = new BlsSignature(new byte[BlsSignature.Length]),
        };

        MessageValidity validity = router.Handle(GossipTopics.ExecutionPayload, gloasTopic: true, Snappy.CompressToArray(SignedExecutionPayloadEnvelope.Encode(envelope)));

        Assert.That(validity, Is.EqualTo(MessageValidity.Rejected), "without the store the block is not held and the envelope is consumed");
    }
}
