// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>A block in a Gloas epoch is stored in the Gloas shape, and every endpoint that reads a block must serve that shape under the gloas version.</summary>
public class BeaconApiGloasBlockTests
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

    private BeaconApiTestHost _host = null!;
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

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    private static readonly string RootHex = Root.ToString();

    // Each of these once read the block through the Fulu-only store accessor, which throws for a Gloas block.
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(data.GetProperty("root").GetString(), Is.EqualTo(RootHex));
            Assert.That(data.GetProperty("canonical").GetBoolean(), Is.True);
            Assert.That(message.GetProperty("slot").GetString(), Is.EqualTo(GloasSlot.ToString()));
            Assert.That(message.GetProperty("proposer_index").GetString(), Is.EqualTo("78"));
            Assert.That(message.GetProperty("parent_root").GetString(), Is.EqualTo(Parent.ToString()));
            Assert.That(message.GetProperty("state_root").GetString(), Is.EqualTo(Hex(32, 0x01)));
            Assert.That(message.GetProperty("body_root").GetString(), Is.EqualTo(SszRoots.HashTreeRoot(_block.Message!.Body!).ToString()));
            Assert.That(header.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x06)));
        }
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
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw);
        JsonElement envelope = JsonDocument.Parse(raw).RootElement;
        JsonElement message = envelope.GetProperty("data").GetProperty("message");
        JsonElement body = message.GetProperty("body");

        // specs/gloas/beacon-chain.md BeaconBlockBody: 13 fields in this order.
        string[] expectedFields =
        [
            "randao_reveal", "eth1_data", "graffiti", "proposer_slashings", "attester_slashings", "attestations", "deposits",
            "voluntary_exits", "sync_aggregate", "bls_to_execution_changes", "signed_execution_payload_bid", "payload_attestations",
            "parent_execution_requests",
        ];

        JsonElement attestation = body.GetProperty("attestations")[0];
        JsonElement firstIndexed = body.GetProperty("attester_slashings")[0].GetProperty("attestation_1");
        JsonElement indexed = body.GetProperty("attester_slashings")[0].GetProperty("attestation_2");
        JsonElement signedBid = body.GetProperty("signed_execution_payload_bid");
        JsonElement bid = signedBid.GetProperty("message");
        JsonElement payloadAttestation = body.GetProperty("payload_attestations")[0];
        JsonElement requests = body.GetProperty("parent_execution_requests");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(envelope.GetProperty("version").GetString(), Is.EqualTo("gloas"));
            Assert.That(message.GetProperty("slot").GetString(), Is.EqualTo(GloasSlot.ToString()));
            Assert.That(message.GetProperty("proposer_index").GetString(), Is.EqualTo("78"));
            Assert.That(message.GetProperty("parent_root").GetString(), Is.EqualTo(Parent.ToString()));
            Assert.That(message.GetProperty("state_root").GetString(), Is.EqualTo(Hex(32, 0x01)));
            Assert.That(envelope.GetProperty("data").GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x06)));
            Assert.That(body.EnumerateObject().Select(p => p.Name), Is.EqualTo(expectedFields));

            Assert.That(body.GetProperty("randao_reveal").GetString(), Is.EqualTo(Hex(96, 0x02)));
            Assert.That(body.GetProperty("eth1_data").GetProperty("deposit_root").GetString(), Is.EqualTo(Hex(32, 0x03)));
            Assert.That(body.GetProperty("graffiti").GetString(), Is.EqualTo(Hex(32, 0x05)));
            Assert.That(body.GetProperty("sync_aggregate").GetProperty("sync_committee_signature").GetString(), Is.EqualTo(Hex(96, 0x71)));
            Assert.That(body.GetProperty("proposer_slashings")[0].GetProperty("signed_header_1").GetProperty("message").GetProperty("body_root").GetString(), Is.EqualTo(Hex(32, 0x13)));
            Assert.That(indexed.GetProperty("attesting_indices").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "4", "5" }));
            Assert.That(indexed.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x42)));
            Assert.That(firstIndexed.GetProperty("attesting_indices").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "1", "4", "5" }));
            Assert.That(firstIndexed.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x41)));
            Assert.That(attestation.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x43)));
            Assert.That(attestation.GetProperty("data").GetProperty("beacon_block_root").GetString(), Is.EqualTo(Hex(32, 0xaa)));
            // Bits {0, 2} of a 3-bit progressive bitlist carry the same sentinel as a bitlist: 0x0d.
            Assert.That(attestation.GetProperty("aggregation_bits").GetString(), Is.EqualTo("0x0d"));
            Assert.That(attestation.GetProperty("committee_bits").GetString(), Is.EqualTo("0x0200000000000000"));
            Assert.That(body.GetProperty("deposits")[0].GetProperty("data").GetProperty("amount").GetString(), Is.EqualTo("32000000000"));
            Assert.That(body.GetProperty("voluntary_exits")[0].GetProperty("message").GetProperty("validator_index").GetString(), Is.EqualTo("9"));
            Assert.That(body.GetProperty("bls_to_execution_changes")[0].GetProperty("message").GetProperty("validator_index").GetString(), Is.EqualTo("11"));

            Assert.That(bid.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[]
            {
                "parent_block_hash", "parent_block_root", "block_hash", "prev_randao", "fee_recipient", "gas_limit", "builder_index",
                "slot", "value", "execution_payment", "blob_kzg_commitments", "execution_requests_root",
            }));
            Assert.That(bid.GetProperty("parent_block_hash").GetString(), Is.EqualTo(Hex(32, 0x81)));
            Assert.That(bid.GetProperty("parent_block_root").GetString(), Is.EqualTo(Parent.ToString()));
            Assert.That(bid.GetProperty("block_hash").GetString(), Is.EqualTo(Hex(32, 0x87)));
            Assert.That(bid.GetProperty("prev_randao").GetString(), Is.EqualTo(Hex(32, 0x86)));
            Assert.That(bid.GetProperty("fee_recipient").GetString(), Is.EqualTo(Hex(20, 0x82)));
            Assert.That(bid.GetProperty("gas_limit").GetString(), Is.EqualTo("30000000"));
            Assert.That(bid.GetProperty("builder_index").GetString(), Is.EqualTo("12"));
            Assert.That(bid.GetProperty("slot").GetString(), Is.EqualTo(GloasSlot.ToString()));
            Assert.That(bid.GetProperty("value").GetString(), Is.EqualTo("1000"));
            Assert.That(bid.GetProperty("execution_payment").GetString(), Is.EqualTo("2000"));
            Assert.That(bid.GetProperty("blob_kzg_commitments").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { Hex(48, 0xa1) }));
            Assert.That(bid.GetProperty("execution_requests_root").GetString(), Is.EqualTo(Hex(32, 0x89)));
            Assert.That(signedBid.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x8a)));

            // Bits 0 and 511 of the PTC_SIZE bitvector: no sentinel.
            Assert.That(payloadAttestation.GetProperty("aggregation_bits").GetString(), Is.EqualTo("0x01" + new string('0', 124) + "80"));
            Assert.That(payloadAttestation.GetProperty("data").GetProperty("beacon_block_root").GetString(), Is.EqualTo(Hex(32, 0x8c)));
            Assert.That(payloadAttestation.GetProperty("data").GetProperty("slot").GetString(), Is.EqualTo((GloasSlot - 1).ToString()));
            Assert.That(payloadAttestation.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x8b)));
            Assert.That(payloadAttestation.GetProperty("data").GetProperty("payload_present").GetBoolean(), Is.True);
            Assert.That(payloadAttestation.GetProperty("data").GetProperty("blob_data_available").GetBoolean(), Is.False);

            Assert.That(requests.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "deposits", "withdrawals", "consolidations", "builder_deposits", "builder_exits" }));
            Assert.That(requests.GetProperty("deposits")[0].GetProperty("index").GetString(), Is.EqualTo("42"));
            Assert.That(requests.GetProperty("builder_deposits")[0].GetProperty("pubkey").GetString(), Is.EqualTo(Hex(48, 0xe1)));
            Assert.That(requests.GetProperty("withdrawals")[0].GetProperty("amount").GetString(), Is.EqualTo("5"));
            Assert.That(requests.GetProperty("consolidations")[0].GetProperty("source_address").GetString(), Is.EqualTo(Hex(20, 0xd1)));
            Assert.That(requests.GetProperty("builder_deposits")[0].GetProperty("withdrawal_credentials").GetString(), Is.EqualTo(Hex(32, 0xe2)));
            Assert.That(requests.GetProperty("builder_deposits")[0].GetProperty("amount").GetString(), Is.EqualTo("3"));
            Assert.That(requests.GetProperty("builder_deposits")[0].GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0xe3)));
            Assert.That(requests.GetProperty("builder_exits")[0].GetProperty("source_address").GetString(), Is.EqualTo(Hex(20, 0xf1)));
            Assert.That(requests.GetProperty("builder_exits")[0].GetProperty("pubkey").GetString(), Is.EqualTo(Hex(48, 0xf2)));
        }
    }

    /// <summary>getBlockAttestationsV2 has no gloas version in v5.0.0-alpha.2; the Gloas Attestation is served under gloas, never fulu.</summary>
    [Test]
    public async Task Block_attestations_are_the_gloas_attestations_under_the_gloas_version()
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/beacon/blocks/{Root}/attestations", Json);
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw);
        JsonElement envelope = JsonDocument.Parse(raw).RootElement;
        JsonElement attestation = envelope.GetProperty("data").EnumerateArray().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(envelope.GetProperty("version").GetString(), Is.EqualTo("gloas"));
            Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("gloas"));
            Assert.That(attestation.GetProperty("aggregation_bits").GetString(), Is.EqualTo("0x0d"));
            Assert.That(attestation.GetProperty("committee_bits").GetString(), Is.EqualTo("0x0200000000000000"));
            Assert.That(attestation.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0x43)));
            Assert.That(attestation.GetProperty("data").GetProperty("beacon_block_root").GetString(), Is.EqualTo(Hex(32, 0xaa)));
        }
    }

    /// <summary>beacon-APIs v5.0.0-alpha.2 getSignedExecutionPayloadEnvelope: the stored envelope under version gloas, with every beta.2 field.</summary>
    [Test]
    public async Task Execution_payload_envelope_json_carries_every_envelope_and_payload_field()
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/execution_payload_envelopes/{Root}", Json);
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw);
        JsonElement envelope = JsonDocument.Parse(raw).RootElement;
        JsonElement data = envelope.GetProperty("data");
        JsonElement message = data.GetProperty("message");
        JsonElement payload = message.GetProperty("payload");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(envelope.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "version", "execution_optimistic", "finalized", "data" }));
            // Fork choice holds the block VALID but not its payload: that status covers only the payload the bid builds on.
            Assert.That(envelope.GetProperty("execution_optimistic").GetBoolean(), Is.True);
            Assert.That((await ReadJsonAsync(await _host.GetAsync($"/eth/v2/beacon/blocks/{Root}", Json))).RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.False);
            Assert.That(envelope.GetProperty("version").GetString(), Is.EqualTo("gloas"));
            Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("gloas"));
            Assert.That(data.GetProperty("signature").GetString(), Is.EqualTo(Hex(96, 0xbb)));
            Assert.That(message.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "payload", "execution_requests", "builder_index", "beacon_block_root", "parent_beacon_block_root" }));
            Assert.That(message.GetProperty("builder_index").GetString(), Is.EqualTo("12"));
            Assert.That(message.GetProperty("beacon_block_root").GetString(), Is.EqualTo(Root.ToString()));
            Assert.That(message.GetProperty("parent_beacon_block_root").GetString(), Is.EqualTo(Parent.ToString()));
            Assert.That(message.GetProperty("execution_requests").GetProperty("builder_exits")[0].GetProperty("pubkey").GetString(), Is.EqualTo(Hex(48, 0xbc)));

            Assert.That(payload.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[]
            {
                "parent_hash", "fee_recipient", "state_root", "receipts_root", "logs_bloom", "prev_randao", "block_number", "gas_limit",
                "gas_used", "timestamp", "extra_data", "base_fee_per_gas", "block_hash", "transactions", "withdrawals", "blob_gas_used",
                "excess_blob_gas", "block_access_list", "slot_number",
            }));
            Assert.That(payload.GetProperty("parent_hash").GetString(), Is.EqualTo(Hex(32, 0xb1)));
            Assert.That(payload.GetProperty("fee_recipient").GetString(), Is.EqualTo(Hex(20, 0xb2)));
            Assert.That(payload.GetProperty("logs_bloom").GetString(), Is.EqualTo(Hex(256, 0xb5)));
            Assert.That(payload.GetProperty("block_number").GetString(), Is.EqualTo("24000000"));
            Assert.That(payload.GetProperty("extra_data").GetString(), Is.EqualTo("0xcafe"));
            Assert.That(payload.GetProperty("base_fee_per_gas").GetString(), Is.EqualTo("9"));
            Assert.That(payload.GetProperty("transactions").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "0x02f8" }));
            Assert.That(payload.GetProperty("withdrawals")[0].GetProperty("validator_index").GetString(), Is.EqualTo("200"));
            Assert.That(payload.GetProperty("excess_blob_gas").GetString(), Is.EqualTo("262144"));
            Assert.That(payload.GetProperty("block_access_list").GetString(), Is.EqualTo("0xc001"));
            Assert.That(payload.GetProperty("slot_number").GetString(), Is.EqualTo(GloasSlot.ToString()));
        }
    }

    /// <summary>
    /// types/primitive.yaml ExecutionOptimistic follows the envelope's own payload: fork choice's verdict on it while fork choice holds the block,
    /// then a verified child that builds on it. A child that built EMPTY over it verifies nothing.
    /// </summary>
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
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine { EnvelopeVerdict = verdict }, snapshots: snapshots, store: store);
        SignedGloasChain.Block parent = chain.Next(null, 32, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(parent, 33, full: validThroughEmptyHead, 0xA2);
        Assert.That(importer.Import(parent.Forked, parent.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        Assert.That(importer.ImportEnvelope(parent.Envelope), Is.EqualTo(verdict == ExecutionStatus.Valid
            ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.Optimistic));
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
        store.PutExecutionPayloadEnvelope(parent.Root, parent.Envelope);

        Assert.That((await ReadJsonAsync(await host.GetAsync(path, Json))).RootElement.GetProperty("execution_optimistic").GetBoolean(),
            Is.EqualTo(expectedOptimistic), "the payload verdict must survive pruning and duplicate storage");
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

    /// <summary>
    /// apis/beacon/execution_payload/envelope_get.yaml: malformed id 400, no envelope (unknown block, a block without one, a
    /// pre-Gloas block) 404, unsupported Accept 406. An unreadable envelope, or one naming another parent than its block, is 500.
    /// </summary>
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
        Assert.That(response.StatusCode, Is.EqualTo(expected));
        Assert.That((await ReadJsonAsync(response)).RootElement.GetProperty("code").GetInt32(), Is.EqualTo((int)expected));
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
                Withdrawals = [new Nethermind.BeaconChain.Types.Withdrawal { Index = 100, ValidatorIndex = 200, Address = new Address(Hex(20, 0xb8)), Amount = 300 }],
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

    /// <summary>A Gloas block with one of every body operation populated, reusing the Fulu fixture's shared operations.</summary>
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
