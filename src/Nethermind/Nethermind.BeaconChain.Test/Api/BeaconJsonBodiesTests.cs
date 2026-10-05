// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Http;
using System.Text.Json;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.Api;

public class BeaconJsonBodiesTests : BeaconApiFixture
{
    private const string Json = "application/json";
    private const string Octet = "application/octet-stream";

    private const ulong Slot = 412_500 * 32 + 7;

    [OneTimeSetUp]
    public async Task StartHost() => _host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet);

    [SetUp]
    public void ResetSharedState()
    {
        _host.StatusHolder.ExecutionInSync = false;
        _host.SetStatus(Hash256.Zero, Hash256.Zero, 0);
    }

    [Test]
    public async Task Block_json_carries_the_versioned_envelope_and_every_body_field_in_beacon_api_encoding()
    {
        Hash256 root = BeaconApiTestHost.TestRoot(0x10);
        _host.Store.PutBlock(root, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.SetCanonicalRoot(Slot, root);

        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/beacon/blocks/{root}", Json);
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(Json));
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("fulu"));

        Assert.That(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))),
            Is.EqualTo("8e53a2bbacd1dcb44aa240c0db316d1e857b52b57048b9b17440db063ec2e60e").IgnoreCase, raw);
    }

    [Test]
    public async Task Block_json_flags_follow_fork_choice_verification_and_finality()
    {
        const ulong slot = Slot + 3;
        Hash256 root = BeaconApiTestHost.TestRoot(0x11);
        ForkChoiceSnapshotNode node = new(slot, root, null, 0, 0, 0, ExecutionStatus.Optimistic, root);
        ForkChoiceSnapshotHolder snapshots = new()
        {
            Current = new ForkChoiceSnapshot(new CheckpointRef(0, Hash256.Zero), new CheckpointRef(0, Hash256.Zero), Hash256.Zero, [node]),
        };
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, snapshots);
        host.Store.PutBlock(root, BeaconApiTestHost.RichBlock(slot, BeaconApiTestHost.FilledHash(0x00)));
        host.Store.SetCanonicalRoot(slot, root);

        host.StatusHolder.ExecutionInSync = true;
        host.SetStatus(root, Hash256.Zero, 0);
        using HttpResponseMessage optimisticResponse = await host.GetAsync($"/eth/v2/beacon/blocks/{root}", Json);
        using JsonDocument optimistic = await BeaconApiTestHost.ReadJsonAsync(optimisticResponse);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(optimistic.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.True);
            Assert.That(optimistic.RootElement.GetProperty("finalized").GetBoolean(), Is.False);
        }

        snapshots.Current = snapshots.Current with { Nodes = [node with { ExecutionStatus = ExecutionStatus.Valid }] };
        host.StatusHolder.ExecutionInSync = false;
        host.SetStatus(root, root, 412_501);
        using HttpResponseMessage confirmedResponse = await host.GetAsync($"/eth/v2/beacon/blocks/{root}", Json);
        using JsonDocument confirmed = await BeaconApiTestHost.ReadJsonAsync(confirmedResponse);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(confirmed.RootElement.GetProperty("execution_optimistic").GetBoolean(), Is.False);
        Assert.That(confirmed.RootElement.GetProperty("finalized").GetBoolean(), Is.True);
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
        _host.Store.SetCanonicalRoot(Slot, root);
        _host.Store.PutBlock(root, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));

        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/debug/beacon/states/{Slot}", Octet);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("fulu"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(ssz), "the checkpoint-provider path must not re-encode");
    }

    [Test]
    public async Task State_json_for_undecodable_persisted_bytes_is_500_with_the_reason_not_a_partial_body()
    {
        Hash256 root = BeaconApiTestHost.TestRoot(0x22);
        _host.Store.PutState(root, [9]);
        _host.Store.SetCanonicalRoot(Slot, root);

        HttpResponseMessage response = await _host.GetAsync($"/eth/v2/debug/beacon/states/{Slot}", Json);
        JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(body.RootElement.GetProperty("message").GetString(), Does.Contain("not decodable"));
    }

    [Test]
    public async Task Headers_by_parent_root_rejects_a_malformed_root()
    {
        HttpResponseMessage malformed = await _host.GetAsync("/eth/v1/beacon/headers?parent_root=0x1234", Json);
        Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Headers_by_parent_root_answers_an_unknown_or_pruned_parent_with_an_empty_list([Values] bool pruned)
    {
        Hash256 parent = BeaconApiTestHost.TestRoot(pruned ? (byte)0x50 : (byte)0xee);
        if (pruned)
        {
            Hash256 child = BeaconApiTestHost.TestRoot(0x51);
            _host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
            _host.Store.PutBlock(child, BeaconApiTestHost.RichBlock(Slot + 1, parent));
            _host.Store.DeleteBlock(child);
            _host.Store.DeleteBlock(parent);
        }

        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", Json);
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        JsonElement body = JsonDocument.Parse(raw).RootElement;
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(body.GetProperty("data").GetArrayLength(), Is.EqualTo(0));
        Assert.That(body.GetProperty("finalized").GetBoolean(), Is.False);
        Assert.That(body.GetProperty("execution_optimistic").GetBoolean(), Is.False, "an empty list references no payload, even while the execution client is not in sync");
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
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
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
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
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
    public async Task Headers_by_parent_root_reports_finalized_only_when_every_child_is_at_or_before_the_checkpoint_slot([Values(0, 5)] int offset)
    {
        ulong checkpointSlot = 412_501 * Presets.SlotsPerEpoch;
        ulong childSlot = checkpointSlot + (ulong)offset;
        Hash256 parent = BeaconApiTestHost.TestRoot(0x50);
        Hash256 child = BeaconApiTestHost.TestRoot(0x51);
        _host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.PutBlock(child, BeaconApiTestHost.RichBlock(childSlot, parent));
        _host.Store.SetCanonicalRoot(childSlot, child);

        _host.SetStatus(child, child, 412_501);
        JsonElement finalized = (await BeaconApiTestHost.ReadJsonAsync(await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", Json))).RootElement;
        Assert.That(finalized.GetProperty("finalized").GetBoolean(), Is.EqualTo(offset == 0));

        _host.SetStatus(child, Hash256.Zero, 412_500);
        JsonElement notFinalized = (await BeaconApiTestHost.ReadJsonAsync(await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", Json))).RootElement;
        Assert.That(notFinalized.GetProperty("finalized").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Headers_by_parent_root_is_not_finalized_when_the_slot_filter_excludes_every_child()
    {
        Hash256 parent = BeaconApiTestHost.TestRoot(0x55);
        Hash256 child = BeaconApiTestHost.TestRoot(0x56);
        _host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.PutBlock(child, BeaconApiTestHost.RichBlock(Slot + 9, parent));
        _host.Store.SetCanonicalRoot(Slot + 9, child);
        _host.SetStatus(child, child, 412_500);

        JsonElement body = (await BeaconApiTestHost.ReadJsonAsync(await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}&slot={Slot + 10}", Json))).RootElement;

        Assert.That(body.GetProperty("data").GetArrayLength(), Is.EqualTo(0));
        Assert.That(body.GetProperty("finalized").GetBoolean(), Is.False, "an empty answer references no finalized history, however finalized the children it filtered out are");
    }

    [Test]
    public async Task Headers_by_parent_root_is_500_for_a_stored_parent_the_index_holds_no_complete_entry_for()
    {
        Hash256 unindexedParent = BeaconApiTestHost.TestRoot(0x60);
        Hash256 child = BeaconApiTestHost.TestRoot(0x61);
        _host.WriteLegacyBlock(unindexedParent, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.PutBlock(child, BeaconApiTestHost.RichBlock(Slot + 1, unindexedParent));

        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={unindexedParent}", Json);
        JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError), "one child is known, but a one-element list from a damaged index would be a lie");
        Assert.That(body.RootElement.GetProperty("message").GetString(), Does.Contain("no complete entry"));
    }

    [Test]
    public async Task Headers_by_parent_root_lists_every_child_of_a_pending_block_that_is_stored_again_or_pruned_and_stored_again([Values] bool prunedFirst)
    {
        Hash256 parent = BeaconApiTestHost.TestRoot(0x62);
        Hash256 child = BeaconApiTestHost.TestRoot(0x63);
        SignedBeaconBlock parentBlock = BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00));
        _host.WriteLegacyBlock(parent, parentBlock);
        _host.Store.PutBlock(child, BeaconApiTestHost.RichBlock(Slot + 1, parent));
        if (prunedFirst)
        {
            _host.Store.DeleteBlock(parent);
        }

        _host.Store.PutBlock(parent, parentBlock);

        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", Json);
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        Assert.That(JsonDocument.Parse(raw).RootElement.GetProperty("data").EnumerateArray().Select(entry => entry.GetProperty("root").GetString()),
            Is.EqualTo(new[] { child.ToString() }));
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

    [Test]
    public async Task State_finalized_flag_uses_the_block_slot_and_the_state_slot_cutoff([Values(0, 5)] int offset)
    {
        const ulong checkpointSlot = 412_500 * Presets.SlotsPerEpoch;
        Hash256 root = BeaconApiTestHost.TestRoot(0x21);
        BeaconStateFulu state = BeaconApiTestHost.RichState(BeaconChainSpec.Mainnet, checkpointSlot + (ulong)offset);
        state.LatestBlockHeader!.Slot = checkpointSlot - 1;
        _host.Store.PutState(root, BeaconStateFulu.Encode(state));
        _host.Store.SetCanonicalRoot(state.LatestBlockHeader.Slot, root);
        _host.SetStatus(root, root, BeaconChainSpec.Mainnet.GetEpoch(checkpointSlot));

        using HttpResponseMessage response = await _host.GetAsync("/eth/v2/debug/beacon/states/head", Json);
        using JsonDocument body = await BeaconApiTestHost.ReadJsonAsync(response);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.RootElement.GetProperty("finalized").GetBoolean(), Is.EqualTo(offset == 0));
    }
}

public class BeaconJsonBodiesGloasTests : BeaconApiFixture
{

    [OneTimeSetUp]
    public async Task StartHost() => _host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Sepolia);

    [Test]
    public async Task Headers_by_parent_root_names_the_fork_only_when_every_listed_child_is_in_the_same_one()
    {
        ulong gloasSlot = BeaconChainSpec.Sepolia.GloasForkEpoch * BeaconChainSpec.Sepolia.SlotsPerEpoch;
        Hash256 parent = BeaconApiTestHost.TestRoot(0x81);
        Hash256 fuluChild = BeaconApiTestHost.TestRoot(0x82);
        Hash256 gloasChild = BeaconApiTestHost.TestRoot(0x83);
        _host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(gloasSlot - 5, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.PutBlock(fuluChild, BeaconApiTestHost.RichBlock(gloasSlot - 2, parent));
        _host.Store.PutForkedBlock(gloasChild, new ForkedSignedBeaconBlock.OfGloas(SignedBeaconBlockBuilders.CreateMinimalGloasBlock(gloasSlot + 1, parent)));

        HttpResponseMessage mixed = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", "application/json");
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(mixed);
        Assert.That(JsonDocument.Parse(raw).RootElement.GetProperty("data").GetArrayLength(), Is.EqualTo(2));
        Assert.That(mixed.Headers.Contains("Eth-Consensus-Version"), Is.False, "children straddle the Gloas boundary, so no single fork name is true of the list");

        HttpResponseMessage gloasOnly = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}&slot={gloasSlot + 1}", "application/json");
        Assert.That(gloasOnly.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(gloasOnly.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("gloas"), "the fork is named for the listed children, not for every child in the index");
    }

}
