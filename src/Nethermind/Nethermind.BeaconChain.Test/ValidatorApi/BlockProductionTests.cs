// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections;
using System.Net;
using Autofac;
using CkzgLib;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.DataAvailability;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using NSubstitute;
using Snappier;

namespace Nethermind.BeaconChain.Test.ValidatorApi;

/// <summary>
/// Block production and publication through <see cref="IBeaconNodeValidatorApi"/> on the real importer, state transition and engine driver,
/// with the execution layer scripted to build the payload its payload attributes describe.
/// </summary>
public class BlockProductionTests
{
    private const int ValidatorCount = 64;
    private const int Proposer = 0;
    private const int WithdrawingValidator = 5;
    private const ulong ExcessBalance = 1_000_000_000;
    private const ulong ProposalSlot = 1;
    private static readonly BeaconChainSpec Spec = ImportableBlobBlock.FuluFromGenesis;
    private static readonly Address FeeRecipient = new(Bytes.FromHexString("0x00000000000000000000000000000000000000fe"));
    private static readonly Hash256 Graffiti = new(Bytes.FromHexString("0x6e65746865726d696e6400000000000000000000000000000000000000000000"));
    private static readonly Hash256 PayloadHash = new(Bytes.FromHexString("0x8888888888888888888888888888888888888888888888888888888888888888"));
    private static readonly UInt256 PayloadValue = 123_456_789;

    private static readonly WithdrawalRequest Request = new()
    {
        SourceAddress = new Address(Bytes.FromHexString("0x00000000000000000000000000000000000000aa")),
        ValidatorPubkey = new BlsPublicKey(Bytes.FromHexString("0xa0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0")),
        Amount = 0,
    };

    [Test]
    public async Task A_produced_block_signed_by_its_proposer_is_published_with_its_data_columns_and_imported_with_every_signature_checked(
        [Values(0, 1)] int blobCount, [Values] bool builtEarly)
    {
        using ProposingNode node = new(blobCount);
        await node.Start(builtEarly ? ProposalSlot - 1 : ProposalSlot, millisecondsIntoSlot: builtEarly ? 8_000 : 100);
        await node.Api.PrepareBeaconProposers([new ProposerPreparation(Proposer, FeeRecipient)], CancellationToken.None);
        AttestationData vote = node.SeedOperations();
        if (builtEarly)
        {
            await node.PrepareNextSlot();
            node.SetClock(ProposalSlot, 100);
        }

        ProducedBlock produced = await node.Produce(node.RandaoReveal(Proposer));
        SignedBeaconBlock signed = node.Sign(produced.Block, Proposer);
        await node.Api.PublishBlock(new SignedBlockContents(signed, produced.Blobs, produced.KzgProofs), CancellationToken.None);
        await node.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        BeaconBlock block = produced.Block;
        BeaconBlockBody body = block.Body!;
        Hash256 root = SszRoots.HashTreeRoot(block);
        byte[] digest = ForkDigest.Compute(Spec, 0);
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That((block.Slot, block.ProposerIndex, block.ParentRoot, body.RandaoReveal, body.Graffiti), Is.EqualTo((ProposalSlot, (ulong)Proposer, node.AnchorRoot, node.RandaoReveal(Proposer), Graffiti)));
        Assert.That((body.ExecutionPayload!.BlockHash, body.ExecutionPayload.FeeRecipient, body.ExecutionPayload.Withdrawals),
            Is.EqualTo((PayloadHash, FeeRecipient, new[] { new SszWithdrawal { Index = 0, ValidatorIndex = WithdrawingValidator, Address = node.WithdrawalAddress, Amount = ExcessBalance } })));
        Assert.That(PayloadConverter.ToExecutionRequestsList(body.ExecutionRequests), Is.EqualTo(node.ExecutionRequests), "the requests round-trip through get_execution_requests");
        Assert.That(body.Attestations!.Select(static attestation => (attestation.Data!.BeaconBlockRoot, attestation.Data.Source!.Root, attestation.AggregationBits!.Cast<bool>().ToArray())),
            Is.EqualTo(new[] { (node.AnchorRoot, vote.Source!.Root, new[] { true, true }) }), "the block pool's aggregate merged with the attestation pool's one; the vote off the justified source left out");
        Assert.That(Enumerable.Range(0, Presets.SyncCommitteeSize).Where(position => body.SyncAggregate!.SyncCommitteeBits![position]), Is.EqualTo(new[] { 0, 1, 2, 3 }));
        Assert.That(body.BlobKzgCommitments!.Select(static commitment => commitment.AsSpan().ToArray()), Is.EqualTo(node.Commitments));
        Assert.That((produced.Blobs, produced.KzgProofs), Is.EqualTo((node.Blobs, node.Proofs)));
        Assert.That(produced.ExecutionPayloadValue, Is.EqualTo(PayloadValue));
        Assert.That(produced.ConsensusBlockValue, Is.GreaterThan(UInt256.Zero), "the attestations and the sync aggregate reward the proposer");
        Assert.That(node.Builds, Is.EqualTo(1), builtEarly ? "the payload of the next slot was built two thirds into the slot before" : "the payload was built when the block was asked for");

        Assert.That(node.Published.Select(static message => message.Topic), Is.EqualTo(new[] { GossipTopics.Topic(digest, GossipTopics.BeaconBlock) }.Concat(Enumerable.Range(0, blobCount == 0 ? 0 : Eip7594DasConstants.NumberOfColumns)
            .Select(column => GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(CustodyGroups.ComputeSubnetForDataColumnSidecar((ulong)column)))))));
        Assert.That(node.Published[0].Message, Is.EqualTo(Snappy.CompressToArray(SignedBeaconBlock.Encode(signed))));
        Assert.That(node.Published.Skip(1).Select(static message => Decode(message.Message)).All(static column => DataColumnSidecarVerifier.VerifyInclusionProof(column) && DataColumnSidecarVerifier.VerifyKzgProofs(column)),
            Is.True, "every published column proves its commitments and cells");
        Assert.That(node.Importer.IsKnown(root), Is.True, "the block passed the importer's state transition with every signature checked");
        Assert.That(node.Heads.Current!.Status.HeadRoot, Is.EqualTo(root), "the node's head moved to its own block");
        Assert.That(node.NewPayloads, Is.EqualTo(new[] { (PayloadHash, blobCount, node.ExecutionRequests) }));
    }

    public enum ProductionFlaw { RandaoOfAnotherValidator, NoFeeRecipient, PayloadOffParent, NoPayload, SlotOfHead }

    [TestCase(ProductionFlaw.RandaoOfAnotherValidator, BeaconNodeApiError.InvalidRequest, "randao reveal is not the signature")]
    [TestCase(ProductionFlaw.NoFeeRecipient, BeaconNodeApiError.InvalidRequest, "No fee recipient is prepared")]
    [TestCase(ProductionFlaw.PayloadOffParent, BeaconNodeApiError.NotReady, "fails its own state transition")]
    [TestCase(ProductionFlaw.NoPayload, BeaconNodeApiError.NotReady, "built no usable payload")]
    [TestCase(ProductionFlaw.SlotOfHead, BeaconNodeApiError.InvalidRequest, "is not after the head")]
    public async Task A_block_the_node_cannot_vouch_for_is_refused_and_nothing_is_published(ProductionFlaw flaw, BeaconNodeApiError expected, string reason)
    {
        using ProposingNode node = new(blobCount: 1);
        await node.Start(flaw == ProductionFlaw.SlotOfHead ? 0 : ProposalSlot, millisecondsIntoSlot: 100);
        if (flaw != ProductionFlaw.NoFeeRecipient) await node.Api.PrepareBeaconProposers([new ProposerPreparation(Proposer, FeeRecipient)], CancellationToken.None);
        node.SeedOperations();
        node.PayloadParent = flaw == ProductionFlaw.PayloadOffParent ? Keccak.Compute("another execution block") : null;
        node.PayloadMissing = flaw == ProductionFlaw.NoPayload;

        BeaconNodeApiException? refusal = flaw == ProductionFlaw.SlotOfHead
            ? await Assert.ThrowsAsync<BeaconNodeApiException>(async () => await node.Api.ProduceBlock(0, node.RandaoReveal(Proposer), Graffiti, CancellationToken.None))
            : await Assert.ThrowsAsync<BeaconNodeApiException>(async () => await node.Produce(node.RandaoReveal(flaw == ProductionFlaw.RandaoOfAnotherValidator ? Proposer + 1 : Proposer)));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(refusal!.Error, Is.EqualTo(expected));
        Assert.That(refusal.Message, Does.Contain(reason));
        Assert.That(node.Published, Is.Empty);
    }

    public enum PublicationFlaw { None, Signature, Proposer, Blob, Proof, AlreadyPublished, BeforeGossip }

    [TestCase(PublicationFlaw.None, null, null)]
    [TestCase(PublicationFlaw.Signature, BeaconNodeApiError.InvalidRequest, "proposer signature is invalid")]
    [TestCase(PublicationFlaw.Proposer, BeaconNodeApiError.InvalidRequest, "is not the proposer")]
    [TestCase(PublicationFlaw.Blob, BeaconNodeApiError.InvalidRequest, "do not match the block's KZG commitments")]
    [TestCase(PublicationFlaw.Proof, BeaconNodeApiError.InvalidRequest, "do not match the block's KZG commitments")]
    [TestCase(PublicationFlaw.AlreadyPublished, BeaconNodeApiError.InvalidRequest, "was already seen")]
    [TestCase(PublicationFlaw.BeforeGossip, BeaconNodeApiError.NotReady, "Gossip has not started")]
    public async Task A_published_block_that_fails_a_gossip_check_is_refused_before_anything_is_published(PublicationFlaw flaw, BeaconNodeApiError? expected, string? reason)
    {
        using ProposingNode node = new(blobCount: 1);
        await node.Start(ProposalSlot, millisecondsIntoSlot: 100);
        await node.Api.PrepareBeaconProposers([new ProposerPreparation(Proposer, FeeRecipient)], CancellationToken.None);
        ProducedBlock produced = await node.Produce(node.RandaoReveal(Proposer));
        BeaconBlock block = produced.Block;
        List<byte[]> blobs = [.. produced.Blobs];
        List<byte[]> proofs = [.. produced.KzgProofs];
        int signer = Proposer;
        switch (flaw)
        {
            case PublicationFlaw.Signature: signer = Proposer + 1; break;
            case PublicationFlaw.Proposer: signer = Proposer + 1; block.ProposerIndex = (ulong)signer; break;
            case PublicationFlaw.Blob: blobs[0] = DataColumnKzgFixture.MakeBlob(0x77); break;
            case PublicationFlaw.Proof: (proofs[0], proofs[1]) = (proofs[1], proofs[0]); break;
            case PublicationFlaw.AlreadyPublished: await node.Api.PublishBlock(new SignedBlockContents(node.Sign(block, Proposer), blobs, proofs), CancellationToken.None); break;
            case PublicationFlaw.BeforeGossip: node.Orchestrator.GossipStarted = false; break;
        }

        int publishedBefore = node.Published.Count;
        BeaconNodeApiException? refusal = null;
        try
        {
            await node.Api.PublishBlock(new SignedBlockContents(node.Sign(block, signer), blobs, proofs), CancellationToken.None);
        }
        catch (BeaconNodeApiException e)
        {
            refusal = e;
        }

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(refusal?.Error, Is.EqualTo(expected));
        if (reason is not null) Assert.That(refusal?.Message, Does.Contain(reason));
        Assert.That(node.Published.Count - publishedBefore, Is.EqualTo(expected is null ? 1 + Eip7594DasConstants.NumberOfColumns : 0));
        Assert.That(node.Orchestrator.QueuedWorkCount, Is.EqualTo(expected is null || flaw == PublicationFlaw.AlreadyPublished ? 1 : 0), "only a published block is queued for import");
    }

    [Test]
    public void Committee_aggregates_merge_when_disjoint_and_combine_into_one_on_chain_attestation_of_each_vote_until_their_epoch_is_too_old()
    {
        ManualTimestamper time = new(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + 40 * Spec.SecondsPerSlot)).UtcDateTime);
        BlockAttestationPool pool = new(Spec, new SlotClock(Spec, time));
        AttestationData vote = Vote(slot: 40, 0x01), otherVote = Vote(slot: 40, 0x02), laterVote = Vote(slot: 41, 0x03);
        BlsSignature[] signatures = [.. Enumerable.Range(0, 8).Select(static fill => GloasTestFixtures.Sign(GloasTestFixtures.ValidatorKey(fill), Keccak.Compute("message")))];

        pool.Add(Aggregate(vote, committee: 3, [true, false, false, true], signatures[0]));
        pool.Add(Aggregate(vote, committee: 3, [false, true, false, false], signatures[1]));
        pool.Add(Aggregate(vote, committee: 3, [true, false, false, false], signatures[2]));
        pool.Add(Aggregate(vote, committee: 1, [true, true], signatures[3]));
        pool.Add(Aggregate(otherVote, committee: 0, [false, true, true], signatures[4]));
        pool.Add(Aggregate(otherVote, committee: 0, [true, true, false], signatures[5]));
        pool.Add(Aggregate(laterVote, committee: 0, [true], signatures[6]));
        pool.Add(new Attestation { AggregationBits = new BitArray(1) { [0] = true }, Data = vote, Signature = signatures[7], CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [1] = true, [2] = true } });

        List<(Attestation Attestation, BlsSignature[] Signatures)> candidates = pool.GetOnChainCandidates(slot: 41);
        time.Add(TimeSpan.FromSeconds(2 * Spec.SlotsPerEpoch * Spec.SecondsPerSlot));
        List<(Attestation Attestation, BlsSignature[] Signatures)> pruned = pool.GetOnChainCandidates(slot: 200);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(candidates.Select(static candidate => (candidate.Attestation.Data!.BeaconBlockRoot, candidate.Attestation.AggregationBits!.Cast<bool>().ToArray(),
                Enumerable.Range(0, Presets.MaxCommitteesPerSlot).Where(committee => candidate.Attestation.CommitteeBits![committee]).ToArray(), candidate.Signatures)),
            Is.EquivalentTo(new[]
            {
                (vote.BeaconBlockRoot, new[] { true, true, true, true, false, true }, new[] { 1, 3 }, new[] { signatures[3], AttestationPool.AggregateSignatures([signatures[0], signatures[1]]) }),
                (otherVote.BeaconBlockRoot, new[] { false, true, true }, new[] { 0 }, new[] { signatures[4] }),
            }),
            "a disjoint aggregate merges, an overlapping one replaces only a smaller one, committees join in index order, and a later slot or a several-committee aggregate is left out");
        Assert.That(pruned, Is.Empty, "a block includes only attestations of its own epoch or the one before");

        static AttestationData Vote(ulong slot, byte fill) => new()
        {
            Slot = slot,
            BeaconBlockRoot = new Hash256(Enumerable.Repeat(fill, Hash256.Size).ToArray()),
            Source = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            Target = new Checkpoint { Epoch = 1, Root = Hash256.Zero },
        };

        static Attestation Aggregate(AttestationData data, int committee, bool[] bits, BlsSignature signature) =>
            new() { AggregationBits = new BitArray(bits), Data = data, Signature = signature, CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [committee] = true } };
    }

    [Test]
    public void A_preparation_holds_through_its_epoch_and_the_two_after_and_a_batch_past_the_bound_is_refused_whole()
    {
        ManualTimestamper time = new(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + 10 * Spec.SlotsPerEpoch * Spec.SecondsPerSlot)).UtcDateTime);
        ProposerPreparations preparations = new(new SlotClock(Spec, time));
        Address other = new(Bytes.FromHexString("0x00000000000000000000000000000000000000ff"));

        bool prepared = preparations.TryPrepare([new ProposerPreparation(1, FeeRecipient), new ProposerPreparation(2, FeeRecipient)]);
        bool replaced = preparations.TryPrepare([new ProposerPreparation(2, other)]);
        bool overBound = preparations.TryPrepare([.. Enumerable.Range(3, ProposerPreparations.MaxPreparations - 1).Select(static index => new ProposerPreparation((ulong)index, FeeRecipient))]);
        time.Add(TimeSpan.FromSeconds(ProposerPreparations.RetainedEpochs * Spec.SlotsPerEpoch * Spec.SecondsPerSlot));
        (Address?, Address?, Address?) lastEpoch = (preparations.GetFeeRecipient(1), preparations.GetFeeRecipient(2), preparations.GetFeeRecipient(3));
        time.Add(TimeSpan.FromSeconds(Spec.SlotsPerEpoch * Spec.SecondsPerSlot));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That((prepared, replaced, overBound), Is.EqualTo((true, true, false)));
        Assert.That(lastEpoch, Is.EqualTo(((Address?)FeeRecipient, (Address?)other, (Address?)null)));
        Assert.That((preparations.GetFeeRecipient(1), preparations.IsEmpty), Is.EqualTo(((Address?)null, true)));
    }

    private static DataColumnSidecar Decode(byte[] message)
    {
        DataColumnSidecar.Decode(Snappy.DecompressToArray(message), out DataColumnSidecar sidecar);
        return sidecar;
    }

    /// <summary>
    /// A node with the real importer, orchestrator, engine driver and validator API on an anchor at slot 0 of <see cref="ValidatorCount"/> validators
    /// with real keys, one of them due a partial withdrawal, and an execution layer that builds the payload its attributes describe, with
    /// <c>blobCount</c> blobs and one withdrawal request, and answers every payload VALID.
    /// </summary>
    private sealed class ProposingNode : IDisposable
    {
        private readonly IContainer _container;
        private readonly ManualTimestamper _time = new();
        private readonly List<(ForkchoiceStateV1 Forkchoice, PayloadAttributes Attributes)> _builds = [];

        public ProposingNode(int blobCount)
        {
            DataColumnKzgFixture.BlobFixture[] fixtures = [.. Enumerable.Range(0, blobCount).Select(static blob => DataColumnKzgFixture.BuildBlob((byte)(0x10 * (blob + 1))))];
            Commitments = [.. fixtures.Select(static blob => blob.Commitment)];
            Blobs = [.. Enumerable.Range(0, blobCount).Select(static blob => DataColumnKzgFixture.MakeBlob((byte)(0x10 * (blob + 1))))];
            Proofs = [.. fixtures.SelectMany(static blob => Enumerable.Range(0, Ckzg.CellsPerExtBlob).Select(cell => blob.Proofs.AsSpan(cell * Ckzg.BytesPerProof, Ckzg.BytesPerProof).ToArray()))];
            ExecutionRequests = PayloadConverter.ToExecutionRequestsList(new ExecutionRequests { Deposits = [], Withdrawals = [Request], Consolidations = [] });
            (State, AnchorBlock, AnchorRoot) = Anchor();

            IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
            engine.engine_forkchoiceUpdatedV3(default!, default).ReturnsForAnyArgs(call =>
            {
                ForkchoiceStateV1 forkchoice = call.ArgAt<ForkchoiceStateV1>(0);
                if (call.ArgAt<PayloadAttributes?>(1) is not { } attributes) return Task.FromResult(ForkchoiceUpdatedV1Result.Valid(null, forkchoice.HeadBlockHash));
                _builds.Add((forkchoice, attributes));
                return Task.FromResult(ForkchoiceUpdatedV1Result.Valid($"0x{_builds.Count:x16}", forkchoice.HeadBlockHash));
            });
            engine.engine_getPayloadV5(default!).ReturnsForAnyArgs(call => Task.FromResult(PayloadMissing
                ? ResultWrapper<GetPayloadV5Result?>.Fail("unknown payload")
                : ResultWrapper<GetPayloadV5Result?>.Success(Build(_builds[(int)BinaryPrimitives.ReadUInt64BigEndian(call.ArgAt<byte[]>(0)) - 1]))));
            engine.engine_newPayloadV4(default!, default!, default, default).ReturnsForAnyArgs(call =>
            {
                NewPayloads.Add((call.ArgAt<ExecutionPayloadV3>(0).BlockHash, call.ArgAt<Hash256?[]>(1).Length, call.ArgAt<byte[][]?>(3)!));
                return Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = call.ArgAt<ExecutionPayloadV3>(0).BlockHash }));
            });
            _container = BeaconChainTestContainer.Builder(engine: engine).AddSingleton(Spec).AddSingleton<ITimestamper>(_time).Build();
            _container.Resolve<PubkeyCache>().Build(State.Validators!);
            _container.Resolve<BeaconDiscovery>().CreateDiscv5Services(IPAddress.Loopback);
            Api.PublishGossip = (topic, message) => Published.Add((topic, message));
        }

        public BeaconStateFulu State { get; }
        public SignedBeaconBlock AnchorBlock { get; }
        public Hash256 AnchorRoot { get; }
        public byte[][] Commitments { get; }
        public byte[][] Blobs { get; }
        public byte[][] Proofs { get; }
        public byte[][] ExecutionRequests { get; }
        public Address WithdrawalAddress => new(State.Validators![WithdrawingValidator].WithdrawalCredentials!.Bytes[12..]);
        public BlockImporter Importer { get; private set; } = null!;
        public BeaconNodeValidatorApi Api => (BeaconNodeValidatorApi)_container.Resolve<IBeaconNodeValidatorApi>();
        public BeaconSyncOrchestrator Orchestrator => _container.Resolve<BeaconSyncOrchestrator>();
        public HeadSnapshotHolder Heads => _container.Resolve<HeadSnapshotHolder>();
        public List<(string Topic, byte[] Message)> Published { get; } = [];
        public List<(Hash256 BlockHash, int BlobCount, byte[][] Requests)> NewPayloads { get; } = [];
        public int Builds => _builds.Count;

        /// <summary>The execution block the built payload names as its parent, or <c>null</c> for the head of the forkchoice state it was asked on.</summary>
        public Hash256? PayloadParent { get; set; }

        public bool PayloadMissing { get; set; }

        /// <summary>Imports the anchor on a wall clock <paramref name="millisecondsIntoSlot"/> into <paramref name="wallSlot"/> and has the execution layer confirm it VALID.</summary>
        public async Task Start(ulong wallSlot, long millisecondsIntoSlot)
        {
            SetClock(wallSlot, millisecondsIntoSlot);
            Heads.CaptureState = true;
            Importer = (BlockImporter)_container.Resolve<IBlockImporterFactory>().Create(new ForkedBeaconState.OfFulu(State), new ForkedSignedBeaconBlock.OfFulu(AnchorBlock), AnchorRoot);
            Orchestrator.Initialize(Importer, new ForkedSignedBeaconBlock.OfFulu(AnchorBlock), AnchorRoot);
            Orchestrator.GossipStarted = true;
            await Orchestrator.RunHeadStepAsync(CancellationToken.None);
        }

        public void SetClock(ulong slot, long millisecondsIntoSlot) =>
            _time.Set(DateTimeOffset.FromUnixTimeMilliseconds((long)(Spec.GenesisTime + slot * Spec.SecondsPerSlot) * 1000 + millisecondsIntoSlot).UtcDateTime);

        /// <summary>Runs the worker's preparation of the next slot, as its timer does two thirds into a slot.</summary>
        public async Task PrepareNextSlot()
        {
            Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.PrepareSlotItem(ProposalSlot));
            await Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        }

        /// <summary>Asks for the block of <see cref="ProposalSlot"/> and runs the worker that builds it.</summary>
        public async Task<ProducedBlock> Produce(BlsSignature randaoReveal)
        {
            Task<ProducedBlock> produced = Api.ProduceBlock(ProposalSlot, randaoReveal, Graffiti, CancellationToken.None).AsTask();
            await Orchestrator.ProcessQueuedAsync(CancellationToken.None);
            return await produced;
        }

        /// <summary>
        /// Pools what a block of <see cref="ProposalSlot"/> can include: committee 0's vote at slot 0 for the anchor, one member's attestation in
        /// the block pool and the other's in the attestation pool, the same vote with a source the state does not justify, and a contribution of
        /// sync committee positions 0 to 3.
        /// </summary>
        /// <returns>The vote a block includes.</returns>
        public AttestationData SeedOperations()
        {
            int[] committee = CommitteeCache.Build(State, 0).GetBeaconCommittee(0, 0).ToArray();
            AttestationData vote = Vote(Hash256.Zero);
            AttestationData offSource = Vote(Keccak.Compute("a source the state does not justify"));
            BlockAttestationPool blockPool = _container.Resolve<BlockAttestationPool>();
            blockPool.Add(new Attestation
            {
                AggregationBits = new BitArray(committee.Length) { [0] = true },
                Data = vote,
                Signature = GloasTestFixtures.AggregateSignature(AttesterSigningRoot(vote), [committee[0]]),
                CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
            });
            _container.Resolve<AttestationPool>().Add(vote, SszRoots.HashTreeRoot(vote), 0, committee.Length, 1, GloasTestFixtures.AggregateSignature(AttesterSigningRoot(vote), [committee[1]]));
            blockPool.Add(new Attestation
            {
                AggregationBits = new BitArray(committee.Length) { [0] = true, [1] = true },
                Data = offSource,
                Signature = GloasTestFixtures.AggregateSignature(AttesterSigningRoot(offSource), committee),
                CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
            });

            Hash256 syncSigningRoot = Domains.ComputeSigningRoot(AnchorRoot, State.GetDomain(DomainType.SyncCommittee, 0));
            _container.Resolve<SyncCommitteePool>().AddContribution(new SyncCommitteeContribution
            {
                Slot = 0,
                BeaconBlockRoot = AnchorRoot,
                SubcommitteeIndex = 0,
                AggregationBits = new BitArray(SyncCommitteeContribution.SubcommitteeSize) { [0] = true, [1] = true, [2] = true, [3] = true },
                Signature = GloasTestFixtures.AggregateSignature(syncSigningRoot, [0, 1, 2, 3]),
            });
            return vote;

            AttestationData Vote(Hash256 sourceRoot) => new()
            {
                Slot = 0,
                BeaconBlockRoot = AnchorRoot,
                Source = new Checkpoint { Epoch = 0, Root = sourceRoot },
                Target = new Checkpoint { Epoch = 0, Root = AnchorRoot },
            };

            Hash256 AttesterSigningRoot(AttestationData data) => Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), State.GetDomain(DomainType.BeaconAttester, 0));
        }

        public BlsSignature RandaoReveal(int validatorIndex) =>
            GloasTestFixtures.Sign(GloasTestFixtures.ValidatorKey(validatorIndex), Domains.ComputeSigningRoot(ImportableBlobBlock.EpochRoot(0), State.GetDomain(DomainType.Randao, 0)));

        public SignedBeaconBlock Sign(BeaconBlock block, int validatorIndex) => new()
        {
            Message = block,
            Signature = GloasTestFixtures.Sign(GloasTestFixtures.ValidatorKey(validatorIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block), State.GetDomain(DomainType.BeaconProposer, 0))),
        };

        public void Dispose() => _container.Dispose();

        private GetPayloadV5Result Build((ForkchoiceStateV1 Forkchoice, PayloadAttributes Attributes) build)
        {
            PayloadAttributes attributes = build.Attributes;
            BlockHeader header = new(PayloadParent ?? build.Forkchoice.HeadBlockHash, Keccak.OfAnEmptySequenceRlp, attributes.SuggestedFeeRecipient!, UInt256.Zero, 1, 30_000_000,
                attributes.Timestamp, [], blobGasUsed: 0, excessBlobGas: 0, parentBeaconBlockRoot: attributes.ParentBeaconBlockRoot)
            {
                Hash = PayloadHash,
                MixHash = attributes.PrevRandao,
                StateRoot = Keccak.Compute("payload state"),
                ReceiptsRoot = Keccak.EmptyTreeHash,
                Bloom = Bloom.Empty,
                BaseFeePerGas = 7,
            };
            return new GetPayloadV5Result(new Block(header, new BlockBody([], [], attributes.Withdrawals)), PayloadValue, new BlobsBundleV2(Commitments, Blobs, Proofs), ExecutionRequests,
                shouldOverrideBuilder: false);
        }

        private static (BeaconStateFulu State, SignedBeaconBlock Block, Hash256 Root) Anchor()
        {
            BeaconStateFulu state = GloasTestFixtures.CreateFuluState(ValidatorCount);
            Validator[] validators = state.Validators!;
            GloasTestFixtures.InstallValidatorKeys(validators, ValidatorCount);
            Validator withdrawing = validators[WithdrawingValidator].Clone();
            withdrawing.WithdrawalCredentials = GloasTestFixtures.EthWithdrawalCredentials(0xee);
            validators[WithdrawingValidator] = withdrawing;
            state.Balances![WithdrawingValidator] += ExcessBalance;
            BlsPublicKey[] committee = [.. Enumerable.Range(0, Presets.SyncCommitteeSize).Select(position => validators[position % ValidatorCount].Pubkey)];
            state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = committee, AggregatePubkey = validators[0].Pubkey };
            state.NextSyncCommittee = new SyncCommittee { Pubkeys = committee, AggregatePubkey = validators[0].Pubkey };
            state.FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero };
            state.LatestExecutionPayloadHeader!.ExtraData = [];
            state.HistoricalSummaries = [];
            state.DepositRequestsStartIndex = Presets.UnsetDepositRequestsStartIndex;

            SignedBeaconBlock anchor = TestChain.CreateBlock(slot: 0, parentRoot: GloasTestFixtures.Hash(0x02));
            BeaconBlock message = anchor.Message!;
            message.Body!.ExecutionPayload!.BlockHash = state.LatestExecutionPayloadHeader.BlockHash;
            // Genesis shape: the first process_slot fills in the header's state root, which makes its root the anchor's.
            state.LatestBlockHeader = ImportableBlobBlock.HeaderFor(message).Message!;
            message.StateRoot = SszRoots.HashTreeRoot(state);
            return (state, anchor, SszRoots.HashTreeRoot(message));
        }
    }
}
