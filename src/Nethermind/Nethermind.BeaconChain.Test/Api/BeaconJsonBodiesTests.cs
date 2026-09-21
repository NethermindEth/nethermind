// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// JSON bodies for <c>/eth/v2/beacon/blocks/{id}</c> and <c>/eth/v2/debug/beacon/states/{id}</c>,
/// and <c>/eth/v1/beacon/headers?parent_root</c>. Expected values are written from the fixture's
/// construction (a bit pattern, a byte fill, a decimal), never read back from the response, so a
/// writer that emits the wrong encoding for a field type fails here rather than passing on shape.
/// </summary>
public class BeaconJsonBodiesTests
{
    private const string Json = "application/json";
    private const string Octet = "application/octet-stream";

    // epoch 412,500: past FuluForkEpoch (411,392) on BeaconChainSpec.Mainnet, so the codec accepts the state and ForkAtEpoch says fulu.
    private const ulong Slot = 412_500 * 32 + 7;

    private BeaconApiTestHost _host = null!;

    [OneTimeSetUp]
    public async Task StartHost() => _host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet);

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [SetUp]
    public void ResetSharedState()
    {
        Metrics.BeaconChainElInSync = 0;
        _host.SetStatus(Hash256.Zero, Hash256.Zero, 0);
    }

    [Test]
    public async Task Block_json_carries_the_versioned_envelope_and_every_body_field_in_beacon_api_encoding()
    {
        Hash256 root = BeaconApiTestHost.TestRoot(0x10);
        _host.Store.PutBlock(root, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.SetCanonicalRoot(Slot, root);

        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/beacon/blocks/{root}", Json);
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw);
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(Json));
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("fulu"));

        JsonElement body = JsonDocument.Parse(raw).RootElement;
        Assert.That(body.GetProperty("version").GetString(), Is.EqualTo("fulu"));
        Assert.That(body.GetProperty("execution_optimistic").GetBoolean(), Is.True, "EL has not confirmed anything in this fixture");
        Assert.That(body.GetProperty("finalized").GetBoolean(), Is.False, "finalized epoch is 0");

        JsonElement message = body.GetProperty("data").GetProperty("message");
        Assert.That(body.GetProperty("data").GetProperty("signature").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(96, 0x06)));
        Assert.That(message.GetProperty("slot").GetString(), Is.EqualTo(Slot.ToString()), "uints are decimal strings");
        Assert.That(message.GetProperty("proposer_index").GetString(), Is.EqualTo("77"));
        Assert.That(message.GetProperty("parent_root").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x00)));
        Assert.That(message.GetProperty("state_root").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x01)));

        JsonElement blockBody = message.GetProperty("body");
        Assert.That(blockBody.GetProperty("randao_reveal").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(96, 0x02)));
        Assert.That(blockBody.GetProperty("eth1_data").GetProperty("deposit_count").GetString(), Is.EqualTo("1234"));
        Assert.That(blockBody.GetProperty("graffiti").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x05)));

        JsonElement proposerSlashing = blockBody.GetProperty("proposer_slashings")[0];
        Assert.That(proposerSlashing.GetProperty("signed_header_1").GetProperty("message").GetProperty("body_root").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x13)));
        Assert.That(proposerSlashing.GetProperty("signed_header_2").GetProperty("signature").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(96, 0x24)));

        JsonElement attesterSlashing = blockBody.GetProperty("attester_slashings")[0];
        Assert.That(attesterSlashing.GetProperty("attestation_1").GetProperty("attesting_indices").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "1", "2", "3" }));
        Assert.That(attesterSlashing.GetProperty("attestation_2").GetProperty("data").GetProperty("target").GetProperty("epoch").GetString(), Is.EqualTo("412499"));

        JsonElement attestation = blockBody.GetProperty("attestations")[0];
        // Bits {0, 2} of a 3-bit list: 0b101 plus the sentinel at bit 3 -> 0x0d. A bitvector encoding (0x05) would be wrong.
        Assert.That(attestation.GetProperty("aggregation_bits").GetString(), Is.EqualTo("0x0d"));
        // Bit 1 of a 64-bit vector, LSB first within the first byte, no sentinel anywhere.
        Assert.That(attestation.GetProperty("committee_bits").GetString(), Is.EqualTo("0x0200000000000000"));
        Assert.That(attestation.GetProperty("data").GetProperty("beacon_block_root").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0xaa)));

        JsonElement deposit = blockBody.GetProperty("deposits")[0];
        Assert.That(deposit.GetProperty("proof").GetArrayLength(), Is.EqualTo(33));
        Assert.That(deposit.GetProperty("proof")[32].GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x30 + 32)));
        Assert.That(deposit.GetProperty("data").GetProperty("pubkey").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(48, 0x51)));
        Assert.That(deposit.GetProperty("data").GetProperty("amount").GetString(), Is.EqualTo("32000000000"));

        JsonElement exit = blockBody.GetProperty("voluntary_exits")[0];
        Assert.That(exit.GetProperty("message").GetProperty("validator_index").GetString(), Is.EqualTo("9"));

        JsonElement sync = blockBody.GetProperty("sync_aggregate");
        Assert.That(sync.GetProperty("sync_committee_bits").GetString(), Is.EqualTo("0x01" + new string('0', 124) + "80"), "bits 0 and 511 of the 512-bit vector");
        Assert.That(sync.GetProperty("sync_committee_signature").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(96, 0x71)));

        JsonElement payload = blockBody.GetProperty("execution_payload");
        Assert.That(payload.GetProperty("fee_recipient").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(20, 0x82)));
        Assert.That(payload.GetProperty("logs_bloom").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(256, 0x85)));
        Assert.That(payload.GetProperty("block_number").GetString(), Is.EqualTo("23000000"));
        Assert.That(payload.GetProperty("extra_data").GetString(), Is.EqualTo("0xc0ffee"));
        Assert.That(payload.GetProperty("base_fee_per_gas").GetString(), Is.EqualTo("7"), "uint256 is a decimal string, not hex");
        Assert.That(payload.GetProperty("transactions").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "0x02f870", "0x01" }));
        JsonElement withdrawal = payload.GetProperty("withdrawals")[0];
        Assert.That(withdrawal.GetProperty("index").GetString(), Is.EqualTo("100"));
        Assert.That(withdrawal.GetProperty("validator_index").GetString(), Is.EqualTo("200"));
        Assert.That(withdrawal.GetProperty("address").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(20, 0x88)));
        Assert.That(withdrawal.GetProperty("amount").GetString(), Is.EqualTo("300"));
        Assert.That(payload.GetProperty("blob_gas_used").GetString(), Is.EqualTo("131072"));

        JsonElement change = blockBody.GetProperty("bls_to_execution_changes")[0].GetProperty("message");
        Assert.That(change.GetProperty("from_bls_pubkey").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(48, 0x91)));
        Assert.That(change.GetProperty("to_execution_address").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(20, 0x92)));

        Assert.That(blockBody.GetProperty("blob_kzg_commitments").EnumerateArray().Select(e => e.GetString()),
            Is.EqualTo(new[] { BeaconApiTestHost.Hex(48, 0xa1), BeaconApiTestHost.Hex(48, 0xa2) }));

        JsonElement requests = blockBody.GetProperty("execution_requests");
        Assert.That(requests.GetProperty("deposits")[0].GetProperty("index").GetString(), Is.EqualTo("42"));
        Assert.That(requests.GetProperty("withdrawals")[0].GetProperty("validator_pubkey").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(48, 0xc2)));
        Assert.That(requests.GetProperty("consolidations")[0].GetProperty("target_pubkey").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(48, 0xd3)));
    }

    [Test]
    public async Task Block_json_execution_optimistic_and_finalized_flags_track_the_drivers_own_signals()
    {
        Hash256 root = BeaconApiTestHost.TestRoot(0x11);
        _host.Store.PutBlock(root, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));

        try
        {
            Metrics.BeaconChainElInSync = 0;
            _host.SetStatus(root, Hash256.Zero, 0);
            JsonElement optimistic = (await BeaconApiTestHost.ReadJsonAsync(await _host.GetAsync($"/eth/v2/beacon/blocks/{root}", Json))).RootElement;
            Assert.That(optimistic.GetProperty("execution_optimistic").GetBoolean(), Is.True, "the EL has not validated the head: a caller must not treat this body as verified");
            Assert.That(optimistic.GetProperty("finalized").GetBoolean(), Is.False);

            Metrics.BeaconChainElInSync = 1;
            _host.SetStatus(root, root, 412_501);
            JsonElement confirmed = (await BeaconApiTestHost.ReadJsonAsync(await _host.GetAsync($"/eth/v2/beacon/blocks/{root}", Json))).RootElement;
            Assert.That(confirmed.GetProperty("execution_optimistic").GetBoolean(), Is.False, "the orchestrator flipped the in-sync gauge after a VALID verdict");
            Assert.That(confirmed.GetProperty("finalized").GetBoolean(), Is.True, "epoch 412,500 is at or before finalized epoch 412,501");
        }
        finally
        {
            Metrics.BeaconChainElInSync = 0;
        }
    }

    [Test]
    public async Task Block_json_and_ssz_are_negotiated_from_the_same_stored_block()
    {
        Hash256 root = BeaconApiTestHost.TestRoot(0x12);
        SignedBeaconBlock block = BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00));
        _host.Store.PutBlock(root, block);

        HttpResponseMessage ssz = await _host.GetAsync($"/eth/v2/beacon/blocks/{root}", Octet);
        Assert.That(ssz.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await ssz.Content.ReadAsByteArrayAsync(), Is.EqualTo(SignedBeaconBlock.Encode(block)));

        HttpResponseMessage json = await _host.GetAsync($"/eth/v2/beacon/blocks/{root}", Json);
        Assert.That(json.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(json.Content.Headers.ContentType?.MediaType, Is.EqualTo(Json));
    }

    [Test]
    public async Task State_json_carries_the_versioned_envelope_and_every_state_field_in_beacon_api_encoding()
    {
        Hash256 root = BeaconApiTestHost.TestRoot(0x20);
        BeaconStateFulu state = BeaconApiTestHost.RichState(BeaconChainSpec.Mainnet, Slot);
        _host.Store.PutState(root, BeaconStateFulu.Encode(state));
        _host.SetStatus(root, Hash256.Zero, 412_497);

        HttpResponseMessage response = await _host.GetAsync("/eth/v2/debug/beacon/states/head", Json);
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw.Length > 500 ? raw[..500] : raw);
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("fulu"));

        JsonElement body = JsonDocument.Parse(raw).RootElement;
        Assert.That(body.GetProperty("version").GetString(), Is.EqualTo("fulu"));
        Assert.That(body.GetProperty("execution_optimistic").GetBoolean(), Is.True);
        Assert.That(body.GetProperty("finalized").GetBoolean(), Is.False, "epoch 412,500 is after finalized epoch 412,497");

        JsonElement data = body.GetProperty("data");
        Assert.That(data.GetProperty("genesis_time").GetString(), Is.EqualTo(BeaconChainSpec.Mainnet.GenesisTime.ToString()));
        Assert.That(data.GetProperty("slot").GetString(), Is.EqualTo(Slot.ToString()));
        Assert.That(data.GetProperty("fork").GetProperty("previous_version").GetString(), Is.EqualTo("0x05000000"));
        Assert.That(data.GetProperty("fork").GetProperty("current_version").GetString(), Is.EqualTo("0x06000000"));
        Assert.That(data.GetProperty("fork").GetProperty("epoch").GetString(), Is.EqualTo("411392"));
        Assert.That(data.GetProperty("latest_block_header").GetProperty("proposer_index").GetString(), Is.EqualTo("8"));
        Assert.That(data.GetProperty("block_roots").GetArrayLength(), Is.EqualTo(8192));
        Assert.That(data.GetProperty("block_roots")[8191].GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x43)));
        Assert.That(data.GetProperty("state_roots").GetArrayLength(), Is.EqualTo(8192));
        Assert.That(data.GetProperty("historical_roots").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { BeaconApiTestHost.Hex(32, 0x46) }));
        Assert.That(data.GetProperty("eth1_data_votes")[0].GetProperty("deposit_count").GetString(), Is.EqualTo("1000"));
        Assert.That(data.GetProperty("eth1_deposit_index").GetString(), Is.EqualTo("998"));

        JsonElement validators = data.GetProperty("validators");
        Assert.That(validators.GetArrayLength(), Is.EqualTo(3));
        Assert.That(validators[1].GetProperty("slashed").GetBoolean(), Is.True, "booleans stay JSON booleans");
        Assert.That(validators[0].GetProperty("slashed").GetBoolean(), Is.False);
        Assert.That(validators[1].GetProperty("exit_epoch").GetString(), Is.EqualTo("412600"));
        Assert.That(validators[0].GetProperty("exit_epoch").GetString(), Is.EqualTo(Presets.FarFutureEpoch.ToString()), "far-future epoch must not overflow or be shortened");
        Assert.That(validators[2].GetProperty("effective_balance").GetString(), Is.EqualTo("2048000000000"));
        Assert.That(validators[2].GetProperty("withdrawal_credentials").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x06)));

        Assert.That(data.GetProperty("balances").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "32000000000", "30999999999", "2048000000001" }));
        Assert.That(data.GetProperty("randao_mixes").GetArrayLength(), Is.EqualTo((int)Presets.EpochsPerHistoricalVector));
        Assert.That(data.GetProperty("slashings").GetArrayLength(), Is.EqualTo((int)Presets.EpochsPerSlashingsVector));
        Assert.That(data.GetProperty("slashings")[1].GetString(), Is.EqualTo("64000000000"));
        // Participation flags are List[uint8]: one decimal per validator, not a packed hex blob.
        Assert.That(data.GetProperty("previous_epoch_participation").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "1", "3", "7" }));
        Assert.That(data.GetProperty("current_epoch_participation").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "0", "2", "255" }));
        Assert.That(data.GetProperty("justification_bits").GetString(), Is.EqualTo("0x09"), "bits {0, 3} of a 4-bit vector, no sentinel");
        Assert.That(data.GetProperty("previous_justified_checkpoint").GetProperty("epoch").GetString(), Is.EqualTo("412497"));
        Assert.That(data.GetProperty("current_justified_checkpoint").GetProperty("root").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x32)));
        Assert.That(data.GetProperty("finalized_checkpoint").GetProperty("root").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x33)));
        Assert.That(data.GetProperty("inactivity_scores").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "0", "4", "0" }));

        JsonElement syncCommittee = data.GetProperty("current_sync_committee");
        Assert.That(syncCommittee.GetProperty("pubkeys").GetArrayLength(), Is.EqualTo(512));
        Assert.That(syncCommittee.GetProperty("aggregate_pubkey").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(48, 0x47)));
        Assert.That(data.GetProperty("next_sync_committee").GetProperty("aggregate_pubkey").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(48, 0x48)));

        JsonElement header = data.GetProperty("latest_execution_payload_header");
        Assert.That(header.GetProperty("base_fee_per_gas").GetString(), Is.EqualTo("1000000007"));
        Assert.That(header.GetProperty("extra_data").GetString(), Is.EqualTo("0xbeef"));
        Assert.That(header.GetProperty("transactions_root").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x58)));
        Assert.That(header.GetProperty("withdrawals_root").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x59)));
        Assert.That(header.GetProperty("excess_blob_gas").GetString(), Is.EqualTo("131072"));
        Assert.That(header.TryGetProperty("transactions", out _), Is.False, "a header carries roots, not the payload lists");

        Assert.That(data.GetProperty("next_withdrawal_index").GetString(), Is.EqualTo("5000000"));
        Assert.That(data.GetProperty("next_withdrawal_validator_index").GetString(), Is.EqualTo("2"));
        Assert.That(data.GetProperty("historical_summaries")[0].GetProperty("state_summary_root").GetString(), Is.EqualTo(BeaconApiTestHost.Hex(32, 0x62)));
        Assert.That(data.GetProperty("deposit_requests_start_index").GetString(), Is.EqualTo("1900000"));
        Assert.That(data.GetProperty("deposit_balance_to_consume").GetString(), Is.EqualTo("10"));
        Assert.That(data.GetProperty("exit_balance_to_consume").GetString(), Is.EqualTo("20"));
        Assert.That(data.GetProperty("earliest_exit_epoch").GetString(), Is.EqualTo("412600"));
        Assert.That(data.GetProperty("consolidation_balance_to_consume").GetString(), Is.EqualTo("30"));
        Assert.That(data.GetProperty("earliest_consolidation_epoch").GetString(), Is.EqualTo("412601"));
        Assert.That(data.GetProperty("pending_deposits")[0].GetProperty("slot").GetString(), Is.EqualTo((Slot - 5).ToString()));
        Assert.That(data.GetProperty("pending_partial_withdrawals")[0].GetProperty("withdrawable_epoch").GetString(), Is.EqualTo("412700"));
        Assert.That(data.GetProperty("pending_consolidations")[0].GetProperty("target_index").GetString(), Is.EqualTo("2"));
        Assert.That(data.GetProperty("proposer_lookahead").GetArrayLength(), Is.EqualTo((int)Presets.ProposerLookaheadSlots));
        Assert.That(data.GetProperty("proposer_lookahead")[5].GetString(), Is.EqualTo("2"));
    }

    [Test]
    public async Task State_ssz_still_serves_the_exact_stored_bytes_and_now_names_the_fork()
    {
        Hash256 root = BeaconApiTestHost.TestRoot(0x21);
        byte[] ssz = BeaconStateFulu.Encode(BeaconApiTestHost.RichState(BeaconChainSpec.Mainnet, Slot));
        _host.Store.PutState(root, ssz);
        _host.Store.PutBlock(root, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));

        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/debug/beacon/states/{root}", Octet);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("fulu"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(ssz), "the checkpoint-provider path must not re-encode");
    }

    [Test]
    public async Task State_json_for_undecodable_persisted_bytes_is_500_with_the_reason_not_a_partial_body()
    {
        Hash256 root = BeaconApiTestHost.TestRoot(0x22);
        _host.Store.PutState(root, [9]);

        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/debug/beacon/states/{root}", Json);
        JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(body.RootElement.GetProperty("message").GetString(), Does.Contain("not decodable"));
    }

    [Test]
    public async Task Headers_by_parent_root_rejects_a_malformed_root_and_404s_an_unknown_parent()
    {
        HttpResponseMessage malformed = await _host.GetAsync("/eth/v1/beacon/headers?parent_root=0x1234", Json);
        Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        HttpResponseMessage unknown = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={BeaconApiTestHost.TestRoot(0xee)}", Json);
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Headers_by_parent_root_lists_every_indexed_child_with_canonical_flags_and_honours_the_slot_filter()
    {
        Hash256 parent = BeaconApiTestHost.TestRoot(0x30);
        Hash256 childA = BeaconApiTestHost.TestRoot(0x31);
        Hash256 childB = BeaconApiTestHost.TestRoot(0x32);
        _host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.PutBlock(childA, BeaconApiTestHost.RichBlock(Slot + 1, parent));
        _host.Store.PutBlock(childB, BeaconApiTestHost.RichBlock(Slot + 2, parent));
        _host.Store.SetCanonicalRoot(Slot, parent);
        _host.Store.SetCanonicalRoot(Slot + 2, childB);

        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", Json);
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw);
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("fulu"));
        JsonElement body = JsonDocument.Parse(raw).RootElement;
        Assert.That(body.GetProperty("finalized").GetBoolean(), Is.False);

        List<JsonElement> entries = [.. body.GetProperty("data").EnumerateArray()];
        Assert.That(entries.Select(e => e.GetProperty("root").GetString()), Is.EquivalentTo(new[] { childA.ToString(), childB.ToString() }));
        foreach (JsonElement entry in entries)
        {
            JsonElement header = entry.GetProperty("header").GetProperty("message");
            Assert.That(header.GetProperty("parent_root").GetString(), Is.EqualTo(parent.ToString()), "every entry really is a child of the queried parent");
            bool isCanonicalChild = entry.GetProperty("root").GetString() == childB.ToString();
            Assert.That(entry.GetProperty("canonical").GetBoolean(), Is.EqualTo(isCanonicalChild), "canonical follows the slot index, not the parent");
        }

        HttpResponseMessage filtered = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}&slot={Slot + 1}", Json);
        JsonElement filteredData = (await BeaconApiTestHost.ReadJsonAsync(filtered)).RootElement.GetProperty("data");
        Assert.That(filteredData.GetArrayLength(), Is.EqualTo(1));
        Assert.That(filteredData[0].GetProperty("root").GetString(), Is.EqualTo(childA.ToString()));

        HttpResponseMessage badSlot = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}&slot=abc", Json);
        Assert.That(badSlot.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Headers_by_parent_root_checks_the_parent_exists_without_decoding_it()
    {
        Hash256 parent = BeaconApiTestHost.TestRoot(0x35);
        Hash256 child = BeaconApiTestHost.TestRoot(0x36);
        _host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.PutBlock(child, BeaconApiTestHost.RichBlock(Slot + 1, parent));
        // Only the children are listed, so the parent's own bytes must never be read: a parent that
        // no longer decodes still has an exact child list to serve.
        _host.Db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(parent.Bytes, [9]);

        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", Json);
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw);
        JsonElement data = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        Assert.That(data.GetArrayLength(), Is.EqualTo(1));
        Assert.That(data[0].GetProperty("root").GetString(), Is.EqualTo(child.ToString()));
    }

    [Test]
    public async Task Headers_by_parent_root_returns_an_honest_empty_list_and_forgets_pruned_children()
    {
        Hash256 parent = BeaconApiTestHost.TestRoot(0x40);
        Hash256 child = BeaconApiTestHost.TestRoot(0x41);
        _host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));

        JsonElement empty = (await BeaconApiTestHost.ReadJsonAsync(await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", Json))).RootElement;
        Assert.That(empty.GetProperty("data").GetArrayLength(), Is.EqualTo(0), "no child has been stored: empty is the true answer");
        Assert.That(empty.GetProperty("finalized").GetBoolean(), Is.False);

        _host.Store.PutBlock(child, BeaconApiTestHost.RichBlock(Slot + 1, parent));
        _host.Store.DeleteBlock(child);

        HttpResponseMessage afterPrune = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", Json);
        Assert.That(afterPrune.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await BeaconApiTestHost.ReadJsonAsync(afterPrune)).RootElement.GetProperty("data").GetArrayLength(), Is.EqualTo(0));
    }

    [Test]
    public async Task Headers_by_parent_root_reports_finalized_only_when_every_child_is_at_or_before_the_finalized_epoch()
    {
        Hash256 parent = BeaconApiTestHost.TestRoot(0x50);
        Hash256 child = BeaconApiTestHost.TestRoot(0x51);
        _host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.PutBlock(child, BeaconApiTestHost.RichBlock(Slot + 1, parent));

        _host.SetStatus(child, child, 412_500);
        JsonElement finalized = (await BeaconApiTestHost.ReadJsonAsync(await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", Json))).RootElement;
        Assert.That(finalized.GetProperty("finalized").GetBoolean(), Is.True, "the only child is in epoch 412,500, which is finalized");

        _host.SetStatus(child, Hash256.Zero, 412_499);
        JsonElement notFinalized = (await BeaconApiTestHost.ReadJsonAsync(await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", Json))).RootElement;
        Assert.That(notFinalized.GetProperty("finalized").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Headers_by_parent_root_is_501_for_a_parent_stored_before_the_index_existed()
    {
        Hash256 legacyParent = BeaconApiTestHost.TestRoot(0x60);
        Hash256 child = BeaconApiTestHost.TestRoot(0x61);
        _host.WriteLegacyBlock(legacyParent, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.PutBlock(child, BeaconApiTestHost.RichBlock(Slot + 1, legacyParent));

        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={legacyParent}", Json);
        JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)501), "one child is known, but siblings stored before the index would be missing: a one-element list would be a lie");
        Assert.That(body.RootElement.GetProperty("message").GetString(), Does.Contain("not indexed"));
    }

    [Test]
    public async Task Headers_without_parent_root_still_answer_by_slot()
    {
        Hash256 root = BeaconApiTestHost.TestRoot(0x70);
        _host.Store.PutBlock(root, BeaconApiTestHost.RichBlock(Slot + 20, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.SetCanonicalRoot(Slot + 20, root);

        JsonElement body = (await BeaconApiTestHost.ReadJsonAsync(await _host.GetAsync($"/eth/v1/beacon/headers?slot={Slot + 20}", Json))).RootElement;
        Assert.That(body.GetProperty("data").GetArrayLength(), Is.EqualTo(1));
        Assert.That(body.GetProperty("data")[0].GetProperty("root").GetString(), Is.EqualTo(root.ToString()));
    }
}

/// <summary>Sepolia schedules Gloas, so a block in a Gloas epoch can exist there; its body layout is not the one this writer serializes.</summary>
public class BeaconJsonBodiesGloasTests
{
    private BeaconApiTestHost _host = null!;

    [OneTimeSetUp]
    public async Task StartHost() => _host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Sepolia);

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [Test]
    public async Task Block_json_for_a_gloas_epoch_block_is_501_while_ssz_still_serves()
    {
        ulong gloasSlot = BeaconChainSpec.Sepolia.GloasForkEpoch * BeaconChainSpec.Sepolia.SlotsPerEpoch + 3;
        Hash256 root = BeaconApiTestHost.TestRoot(0x80);
        _host.Store.PutBlock(root, BeaconApiTestHost.RichBlock(gloasSlot, BeaconApiTestHost.FilledHash(0x00)));

        HttpResponseMessage json = await _host.GetAsync($"/eth/v2/beacon/blocks/{root}", "application/json");
        JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(json);
        Assert.That(json.StatusCode, Is.EqualTo((HttpStatusCode)501));
        Assert.That(body.RootElement.GetProperty("message").GetString(), Does.Contain("gloas"));

        HttpResponseMessage ssz = await _host.GetAsync($"/eth/v2/beacon/blocks/{root}", "application/octet-stream");
        Assert.That(ssz.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
