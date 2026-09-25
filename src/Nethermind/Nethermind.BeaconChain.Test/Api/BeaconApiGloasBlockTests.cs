// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

    private BeaconApiTestHost _host = null!;
    private SignedBeaconBlockGloas _block = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await StartAsync(Spec);
        _block = RichGloasBlock(GloasSlot, Parent);
        _host.Store.PutBlock(Parent, MinimalBlock(GloasSlot - 40));
        _host.Store.PutForkedBlock(Root, new ForkedSignedBeaconBlock.OfGloas(_block));
        _host.Store.SetCanonicalRoot(GloasSlot, Root);
        _host.Store.PutState(Root, [0x01, 0x02, 0x03]);
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
    [TestCase("/eth/v2/debug/beacon/states/{0}", OctetStream, true)]
    [TestCase("/eth/v1/beacon/states/{0}/root", Json, false)]
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
    public async Task State_root_by_block_id_reads_the_gloas_block_state_root()
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{Root}/root", Json);
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
