// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Merge.Plugin.Data;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.Api;

public class BeaconApiGloasBlockTests : BeaconApiFixture
{
    private const string Json = "application/json";
    private const string OctetStream = "application/octet-stream";

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Sepolia;
    private static readonly ulong GloasSlot = Spec.GloasForkEpoch * Spec.SlotsPerEpoch + 3;
    private static readonly Hash256 Parent = TestRoot(0x90);
    private static readonly Hash256 Root = TestRoot(0x91);
    private static readonly Hash256 NoEnvelopeRoot = TestRoot(0x92);
    private static readonly Hash256 MismatchedEnvelopeRoot = TestRoot(0x93);
    private static readonly Hash256 UnreadableEnvelopeRoot = TestRoot(0x94);

    private SignedBeaconBlockGloas _block = null!;
    private SignedExecutionPayloadEnvelope _envelope = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        // Fork choice reports the block itself verified, so a flag that follows the block would read false.
        ForkChoiceSnapshotHolder forkChoice = new()
        {
            Current = new ForkChoiceSnapshot(new CheckpointRef(0, Parent), new CheckpointRef(0, Parent), Hash256.Zero,
                [new ForkChoiceSnapshotNode(GloasSlot, Root, Parent, 0, 0, 0, ExecutionStatus.Valid, FilledHash(0x87))]),
        };
        _host = await StartAsync(Spec, forkChoice);
        _block = RichGloasBlock(GloasSlot, Parent);
        _host.Store.PutBlock(Parent, MinimalBlock(GloasSlot - 40));
        _host.Store.PutForkedBlock(Root, new ForkedSignedBeaconBlock.OfGloas(_block));
        _host.Store.SetCanonicalRoot(GloasSlot, Root);
        _host.Store.PutState(Root, [0x01, 0x02, 0x03]);

        _envelope = RichEnvelope(Root, Parent, GloasSlot);
        _host.Store.PutExecutionPayloadEnvelope(Root, _envelope);
        _host.Store.PutForkedBlock(NoEnvelopeRoot, new ForkedSignedBeaconBlock.OfGloas(RichGloasBlock(GloasSlot + 1, Root)));
        _host.Store.SetCanonicalRoot(GloasSlot + 1, NoEnvelopeRoot);
        _host.Store.PutForkedBlock(MismatchedEnvelopeRoot, new ForkedSignedBeaconBlock.OfGloas(RichGloasBlock(GloasSlot + 2, NoEnvelopeRoot)));
        _host.Store.PutExecutionPayloadEnvelope(MismatchedEnvelopeRoot, RichEnvelope(MismatchedEnvelopeRoot, FilledHash(0x99), GloasSlot + 2));
        _host.Store.PutForkedBlock(UnreadableEnvelopeRoot, new ForkedSignedBeaconBlock.OfGloas(RichGloasBlock(GloasSlot + 3, MismatchedEnvelopeRoot)));
        _host.Db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes).Set(UnreadableEnvelopeRoot.Bytes, [0x01, 0x02, 0x03]);
    }

    private static readonly string RootHex = Root.ToString();

    [TestCase("/eth/v1/beacon/headers/{0}", Json, true)]
    [TestCase("/eth/v1/beacon/headers?slot={1}", Json, true)]
    [TestCase("/eth/v1/beacon/headers?parent_root={2}", Json, true)]
    [TestCase("/eth/v1/beacon/blocks/{0}/root", Json, true)]
    [TestCase("/eth/v2/beacon/blocks/{0}", Json, true)]
    [TestCase("/eth/v2/beacon/blocks/{0}", OctetStream, true)]
    [TestCase("/eth/v2/beacon/blocks/{0}/attestations", Json, true)]
    [TestCase("/eth/v2/debug/beacon/states/{1}", OctetStream, true)]
    [TestCase("/eth/v1/beacon/states/{1}/root", Json, false)]
    public async Task Every_block_reading_endpoint_serves_a_gloas_root_instead_of_failing(string template, string accept, bool namesTheFork)
    {
        HttpResponseMessage response = await _host.GetAsync(string.Format(template, RootHex, GloasSlot, Parent), accept);
        string raw = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw.Length > 500 ? raw[..500] : raw);
        if (namesTheFork)
        {
            Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("gloas"));
        }
    }

    [Test]
    public async Task Header_is_built_from_the_gloas_block_and_commits_to_the_gloas_body()
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/headers/{Root}", Json);
        JsonElement data = (await ReadJsonAsync(response)).RootElement.GetProperty("data");
        JsonElement header = data.GetProperty("header");
        JsonElement message = header.GetProperty("message");

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(data.GetProperty("root").GetString(), Is.EqualTo(RootHex));
        Assert.That(data.GetProperty("canonical").GetBoolean(), Is.True);
        Assert.That(message.GetProperty("slot").GetString(), Is.EqualTo(GloasSlot.ToString()));
        Assert.That(message.GetProperty("proposer_index").GetString(), Is.EqualTo("78"));
        Assert.That(message.GetProperty("parent_root").GetString(), Is.EqualTo(Parent.ToString()));
        Assert.That(message.GetProperty("state_root").GetString(), Is.EqualTo(Hex(32, 0x01)));
        Assert.That(message.GetProperty("body_root").GetString(), Is.EqualTo(SszRoots.HashTreeRoot(_block.Message!.Body!).ToString()));
        Assert.That(header.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x06)));
    }

    [Test]
    public async Task State_root_by_slot_reads_the_gloas_block_state_root()
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{GloasSlot}/root", Json);
        JsonElement data = (await ReadJsonAsync(response)).RootElement.GetProperty("data");

        Assert.That(data.GetProperty("root").GetString(), Is.EqualTo(Hex(32, 0x01)));
    }

    [Test]
    public async Task Block_ssz_is_the_gloas_encoding_of_the_stored_block()
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/beacon/blocks/{Root}", OctetStream);

        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(SignedBeaconBlockGloas.Encode(_block)));
    }

    [Test]
    public async Task Block_json_carries_every_gloas_body_field_and_none_the_fork_removed()
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/beacon/blocks/{Root}", Json);
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        BeaconApiTestHost.AssertJsonDigest(raw, "dd2c64054c77edc6609347506e720bf0be87076c8a041fb4067aaee9f38f5c52");
    }

    /// <summary>getBlockAttestationsV2 has no gloas version in v5.0.0-alpha.2; the Gloas Attestation is served under gloas, never fulu.</summary>
    [Test]
    public async Task Block_attestations_are_the_gloas_attestations_under_the_gloas_version()
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/beacon/blocks/{Root}/attestations", Json);
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        JsonElement envelope = JsonDocument.Parse(raw).RootElement;
        JsonElement attestation = envelope.GetProperty("data").EnumerateArray().Single();

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(envelope.GetProperty("version").GetString(), Is.EqualTo("gloas"));
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("gloas"));
        Assert.That(attestation.GetProperty("aggregation_bits").GetString(), Is.EqualTo("0x0d"));
        Assert.That(attestation.GetProperty("committee_bits").GetString(), Is.EqualTo("0x0200000000000000"));
        Assert.That(attestation.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x43)));
        Assert.That(attestation.GetProperty("data").GetProperty("beacon_block_root").GetString(), Is.EqualTo(Hex(32, 0xaa)));
    }

    [Test]
    public async Task Execution_payload_envelope_json_carries_every_envelope_and_payload_field()
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/execution_payload_envelopes/{Root}", Json);
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        JsonElement envelope = JsonDocument.Parse(raw).RootElement;
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        // Fork choice holds the block VALID but not its payload: that status covers only the payload the bid builds on.
        Assert.That(envelope.GetProperty("execution_optimistic").GetBoolean(), Is.True);
        Assert.That((await ReadJsonAsync(await _host.GetAsync($"/eth/v2/beacon/blocks/{Root}", Json))).RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.False);
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("gloas"));
        BeaconApiTestHost.AssertJsonDigest(raw, "74bc619fb686804b52f870dd9eeb027611023d57728c03725a45172dac1d4d6a");
    }

    /// <summary>types/primitive.yaml ties ExecutionOptimistic to the envelope payload: its fork-choice
    /// verdict while held, then a verified child building on it. An EMPTY child verifies nothing.</summary>
    [TestCase(true, true, true, false, TestName = "Held block with a verified payload")]
    [TestCase(true, false, true, true, TestName = "Held VALID block with an unverified payload")]
    [TestCase(false, true, true, false, TestName = "Pruned block with a verified child built on its payload")]
    [TestCase(false, true, false, true, TestName = "Pruned block with an unverified child built on its payload")]
    [TestCase(false, false, true, true, TestName = "Pruned block with a verified child built EMPTY over its payload")]
    public async Task Execution_payload_envelope_optimism_follows_its_own_payload(bool heldByForkChoice, bool payloadVerifiedOrBuiltOn, bool childVerified, bool expectedOptimistic)
    {
        Hash256 childRoot = TestRoot(0x95);
        SignedBeaconBlockGloas child = RichGloasBlock(GloasSlot + 1, Root);
        child.Message!.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockHash = payloadVerifiedOrBuiltOn ? FilledHash(0x87) : FilledHash(0x81);
        ForkChoiceSnapshotNode node = heldByForkChoice
            ? new ForkChoiceSnapshotNode(GloasSlot, Root, Parent, 0, 0, 0, ExecutionStatus.Valid, FilledHash(0x87), PayloadValid: payloadVerifiedOrBuiltOn)
            : new ForkChoiceSnapshotNode(GloasSlot + 1, childRoot, Root, 0, 0, 0, childVerified ? ExecutionStatus.Valid : ExecutionStatus.Optimistic, FilledHash(0x88));
        ForkChoiceSnapshotHolder forkChoice = new()
        {
            Current = new ForkChoiceSnapshot(new CheckpointRef(0, Parent), new CheckpointRef(0, Parent), Hash256.Zero, [node]),
        };
        await using BeaconApiTestHost host = await StartAsync(Spec, forkChoice);
        host.Store.PutBlock(Parent, MinimalBlock(GloasSlot - 40));
        host.Store.PutForkedBlock(Root, new ForkedSignedBeaconBlock.OfGloas(_block));
        host.Store.PutExecutionPayloadEnvelope(Root, _envelope);
        host.Store.PutForkedBlock(childRoot, new ForkedSignedBeaconBlock.OfGloas(child));

        HttpResponseMessage response = await host.GetAsync($"/eth/v1/beacon/execution_payload_envelopes/{Root}", Json);

        Assert.That((await ReadJsonAsync(response)).RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.EqualTo(expectedOptimistic));
    }

    [TestCase(ExecutionStatus.Valid, false, false)]
    [TestCase(ExecutionStatus.Optimistic, false, false)]
    [TestCase(ExecutionStatus.Optimistic, true, false)]
    [TestCase(ExecutionStatus.Optimistic, true, true)]
    public async Task Pruned_envelope_keeps_only_its_own_valid_verdict(ExecutionStatus verdict, bool laterValid, bool validThroughEmptyHead)
    {
        SignedGloasChain chain = new();
        ForkChoiceSnapshotHolder snapshots = new();
        await using BeaconApiTestHost host = await StartAsync(chain.Spec, snapshots);
        BeaconChainStore store = new(host.Db, chain.Spec);
        ExecutionPayloadEnvelopePool pool = new(store: store);
        MemDb envelopes = (MemDb)host.Db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine { EnvelopeVerdict = verdict }, snapshots: snapshots, store: store);
        SignedGloasChain.Block parent = chain.Next(null, 32, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(parent, 33, full: validThroughEmptyHead, 0xA2);
        Assert.That(importer.Import(parent.Forked, parent.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        long writesBefore = envelopes.WritesCount;
        Assert.That(importer.ImportEnvelope(parent.Envelope), Is.EqualTo(verdict == ExecutionStatus.Valid
            ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.Optimistic));
        pool.Add(parent.Root, parent.Envelope, persisted: true);
        Assert.That(envelopes.WritesCount - writesBefore, Is.EqualTo(verdict == ExecutionStatus.Valid ? 4 : 3),
            "one envelope write, its slot index and bounds, and a marker only for VALID");
        if (laterValid && !validThroughEmptyHead)
            importer.OnForkchoiceUpdated(parent.Root, parent.Bid.BlockHash!, new PayloadStatusV1 { Status = PayloadStatus.Valid });
        Assert.That(importer.Import(child.Forked, child.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        if (validThroughEmptyHead)
        {
            HeadView head = importer.ComputeHead();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(head.HeadRoot, Is.EqualTo(child.Root));
                Assert.That(head.HeadExecutionHash, Is.EqualTo(parent.Bid.BlockHash), "the EMPTY head uses the payload its bid builds on");
                Assert.That(head.HeadPayloadFull, Is.False, "the child's envelope has not arrived");
            }
            importer.OnForkchoiceUpdated(head.HeadRoot, head.HeadExecutionHash!, new PayloadStatusV1 { Status = PayloadStatus.Valid });
        }
        importer.ComputeHead();
        bool expectedOptimistic = verdict != ExecutionStatus.Valid && !laterValid;
        string path = $"/eth/v1/beacon/execution_payload_envelopes/{parent.Root}";
        Assert.That((await ReadJsonAsync(await host.GetAsync(path, Json))).RootElement.GetProperty("execution_optimistic").GetBoolean(),
            Is.EqualTo(expectedOptimistic), "the held payload's own verdict");

        ForkChoiceSnapshot snapshot = snapshots.Current!;
        snapshots.Current = snapshot with { Nodes = snapshot.Nodes.Where(n => n.Root != parent.Root).ToArray() };

        Assert.That((await ReadJsonAsync(await host.GetAsync(path, Json))).RootElement.GetProperty("execution_optimistic").GetBoolean(),
            Is.EqualTo(expectedOptimistic), "the payload verdict must survive removal from fork choice");
        snapshots.Current = null;
        Assert.That((await ReadJsonAsync(await host.GetAsync(path, Json))).RootElement.GetProperty("execution_optimistic").GetBoolean(),
            Is.EqualTo(expectedOptimistic), "the persisted verdict also survives the absence of a published fork choice snapshot");
    }

    [Test]
    public async Task Execution_payload_envelope_ssz_is_the_stored_envelope_encoding()
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/execution_payload_envelopes/{GloasSlot}", OctetStream);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("gloas"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(SignedExecutionPayloadEnvelope.Encode(_envelope)));
    }

    [TestCase("not-a-block", Json, HttpStatusCode.BadRequest)]
    [TestCase("0x00000000000000000000000000000000000000000000000000000000000000ee", Json, HttpStatusCode.NotFound)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000092", Json, HttpStatusCode.NotFound)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000090", Json, HttpStatusCode.NotFound)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000093", Json, HttpStatusCode.InternalServerError)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000094", Json, HttpStatusCode.InternalServerError)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000091", "text/plain", HttpStatusCode.NotAcceptable)]
    public async Task Execution_payload_envelope_errors_follow_the_published_responses(string blockId, string accept, HttpStatusCode expected)
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/execution_payload_envelopes/{blockId}", accept);
        await BeaconApiTestHost.AssertErrorAsync(response, expected);
    }

    private static SignedExecutionPayloadEnvelope RichEnvelope(Hash256 blockRoot, Hash256 parent, ulong slot) => new()
    {
        Message = new ExecutionPayloadEnvelope
        {
            Payload = new ExecutionPayloadGloas
            {
                ParentHash = FilledHash(0xb1),
                FeeRecipient = new Address(Hex(20, 0xb2)),
                StateRoot = FilledHash(0xb3),
                ReceiptsRoot = FilledHash(0xb4),
                LogsBloom = new Bloom(Enumerable.Repeat((byte)0xb5, 256).ToArray()),
                PrevRandao = FilledHash(0xb6),
                BlockNumber = 24_000_000,
                GasLimit = 36_000_000,
                GasUsed = 21_000,
                Timestamp = 1_760_000_000,
                ExtraData = [0xca, 0xfe],
                BaseFeePerGas = 9,
                BlockHash = FilledHash(0xb7),
                Transactions = [new TransactionGloas { Bytes = [0x02, 0xf8] }],
                Withdrawals = [new Nethermind.Merge.Plugin.SszRest.SszWithdrawal { Index = 100, ValidatorIndex = 200, Address = new Address(Hex(20, 0xb8)), Amount = 300 }],
                BlobGasUsed = 131_072,
                ExcessBlobGas = 262_144,
                BlockAccessList = [0xc0, 0x01],
                SlotNumber = slot,
            },
            ExecutionRequests = new ExecutionRequestsGloas
            {
                Deposits = [],
                Withdrawals = [],
                Consolidations = [],
                BuilderDeposits = [],
                BuilderExits = [new BuilderExitRequest { SourceAddress = new Address(Hex(20, 0xb9)), Pubkey = FilledPubkey(0xbc) }],
            },
            BuilderIndex = 12,
            BeaconBlockRoot = blockRoot,
            ParentBeaconBlockRoot = parent,
        },
        Signature = FilledSignature(0xbb),
    };

    private static SignedBeaconBlockGloas RichGloasBlock(ulong slot, Hash256 parent)
    {
        BeaconBlockBody fulu = RichBlock(slot, parent).Message!.Body!;
        Attestation fuluAttestation = fulu.Attestations![0];
        BitArray ptcBits = new(512);
        ptcBits[0] = true;
        ptcBits[511] = true;
        IndexedAttestationGloas Indexed(ulong[] indices, byte signature) =>
            new() { AttestingIndices = indices, Data = fuluAttestation.Data, Signature = FilledSignature(signature) };

        return new SignedBeaconBlockGloas
        {
            Message = new BeaconBlockGloas
            {
                Slot = slot,
                ProposerIndex = 78,
                ParentRoot = parent,
                StateRoot = FilledHash(0x01),
                Body = new BeaconBlockBodyGloas
                {
                    RandaoReveal = fulu.RandaoReveal,
                    Eth1Data = fulu.Eth1Data,
                    Graffiti = fulu.Graffiti,
                    ProposerSlashings = fulu.ProposerSlashings,
                    AttesterSlashings = [new AttesterSlashingGloas { Attestation1 = Indexed([1, 4, 5], 0x41), Attestation2 = Indexed([4, 5], 0x42) }],
                    Attestations =
                    [
                        new AttestationGloas
                        {
                            AggregationBits = fuluAttestation.AggregationBits,
                            Data = fuluAttestation.Data,
                            Signature = fuluAttestation.Signature,
                            CommitteeBits = fuluAttestation.CommitteeBits,
                        },
                    ],
                    Deposits = fulu.Deposits,
                    VoluntaryExits = fulu.VoluntaryExits,
                    SyncAggregate = fulu.SyncAggregate,
                    BlsToExecutionChanges = fulu.BlsToExecutionChanges,
                    SignedExecutionPayloadBid = new SignedExecutionPayloadBid
                    {
                        Message = new ExecutionPayloadBid
                        {
                            ParentBlockHash = FilledHash(0x81),
                            ParentBlockRoot = parent,
                            BlockHash = FilledHash(0x87),
                            PrevRandao = FilledHash(0x86),
                            FeeRecipient = new Address(Hex(20, 0x82)),
                            GasLimit = 30_000_000,
                            BuilderIndex = 12,
                            Slot = slot,
                            Value = 1000,
                            ExecutionPayment = 2000,
                            BlobKzgCommitments = [fulu.BlobKzgCommitments![0]],
                            ExecutionRequestsRoot = FilledHash(0x89),
                        },
                        Signature = FilledSignature(0x8a),
                    },
                    PayloadAttestations =
                    [
                        new PayloadAttestation
                        {
                            AggregationBits = ptcBits,
                            Data = new PayloadAttestationData { BeaconBlockRoot = FilledHash(0x8c), Slot = slot - 1, PayloadPresent = true, BlobDataAvailable = false },
                            Signature = FilledSignature(0x8b),
                        },
                    ],
                    ParentExecutionRequests = new ExecutionRequestsGloas
                    {
                        Deposits = fulu.ExecutionRequests!.Deposits,
                        Withdrawals = fulu.ExecutionRequests.Withdrawals,
                        Consolidations = fulu.ExecutionRequests.Consolidations,
                        BuilderDeposits = [new BuilderDepositRequest { Pubkey = FilledPubkey(0xe1), WithdrawalCredentials = FilledHash(0xe2), Amount = 3, Signature = FilledSignature(0xe3) }],
                        BuilderExits = [new BuilderExitRequest { SourceAddress = new Address(Hex(20, 0xf1)), Pubkey = FilledPubkey(0xf2) }],
                    },
                },
            },
            Signature = FilledSignature(0x06),
        };
    }
}
