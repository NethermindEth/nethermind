// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

public class BeaconStatesQueuesTests : BeaconApiFixture
{
    private const string Json = "application/json";
    private const string Octet = "application/octet-stream";

    private const ulong StateEpoch = 412_500;
    private const ulong StateSlot = StateEpoch * 32;

    private const ulong PreElectraEpoch = 300_000;
    private const ulong ElectraEpoch = 400_000;

    private static readonly Hash256 StateRoot = BeaconApiTestHost.TestRoot(0x60);

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet);
        _host.Store.PutState(StateRoot, BeaconStateFulu.Encode(QueueState()));
        _host.Store.PutBlock(StateRoot, BeaconApiTestHost.MinimalBlock(StateSlot));
        _host.Store.SetCanonicalRoot(StateSlot, StateRoot);
    }

    [SetUp]
    public void ResetSharedState() => _host.SetStatus(StateRoot, Hash256.Zero, 0);

    public static IEnumerable<TestCaseData> JsonCases()
    {
        yield return new TestCaseData("pending_deposits", new[]
        {
            $"pubkey={Hex(48, 0x71)};withdrawal_credentials={Hex(32, 0x72)};amount=1000000000;signature={Hex(96, 0x73)};slot={StateSlot - 5}",
            $"pubkey={Hex(48, 0x74)};withdrawal_credentials={Hex(32, 0x75)};amount=32000000000;signature={Hex(96, 0x76)};slot={StateSlot - 3}",
        }).SetName("Pending_deposits_json");
        yield return new TestCaseData("pending_partial_withdrawals", new[]
        {
            "validator_index=2;amount=1000;withdrawable_epoch=412700",
            "validator_index=0;amount=7;withdrawable_epoch=412701",
        }).SetName("Pending_partial_withdrawals_json");
        yield return new TestCaseData("pending_consolidations", new[]
        {
            "source_index=0;target_index=2",
            "source_index=1;target_index=2",
        }).SetName("Pending_consolidations_json");
    }

    [TestCaseSource(nameof(JsonCases))]
    public async Task Queue_json_is_versioned_and_keeps_state_order(string endpoint, string[] expected)
    {
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{StateSlot}/{endpoint}", Json);
        JsonElement root = await ReadVersionedEnvelope(response, expectedFinalized: false);
        string[] actual = [.. root.GetProperty("data").EnumerateArray().Select(Flatten)];
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public async Task Proposer_lookahead_json_lists_every_slot_as_a_decimal_string()
    {
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{StateSlot}/proposer_lookahead", Json);
        JsonElement root = await ReadVersionedEnvelope(response, expectedFinalized: false);
        string[] actual = [.. root.GetProperty("data").EnumerateArray().Select(e => e.GetString()!)];
        Assert.That(actual, Is.EqualTo(Enumerable.Range(0, 64).Select(i => LookaheadAt(i).ToString()).ToArray()));
    }

    [Test]
    public async Task Finalized_flag_follows_the_finalized_checkpoint(
        [Values("pending_deposits", "pending_partial_withdrawals", "pending_consolidations", "proposer_lookahead")] string endpoint)
    {
        _host.SetStatus(StateRoot, StateRoot, StateEpoch);
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{StateSlot}/{endpoint}", Json);
        await ReadVersionedEnvelope(response, expectedFinalized: true);
    }

    public static IEnumerable<TestCaseData> SszCases()
    {
        QueueFixture f = new();
        yield return new TestCaseData("pending_deposits", f.Deposits.SelectMany(d => PendingDeposit.Encode(d)).ToArray(), 192).SetName("Pending_deposits_ssz");
        yield return new TestCaseData("pending_partial_withdrawals", f.Withdrawals.SelectMany(w => PendingPartialWithdrawal.Encode(w)).ToArray(), 24).SetName("Pending_partial_withdrawals_ssz");
        yield return new TestCaseData("pending_consolidations", f.Consolidations.SelectMany(c => PendingConsolidation.Encode(c)).ToArray(), 16).SetName("Pending_consolidations_ssz");

        byte[] lookahead = new byte[64 * sizeof(ulong)];
        for (int i = 0; i < 64; i++) BinaryPrimitives.WriteUInt64LittleEndian(lookahead.AsSpan(i * sizeof(ulong)), LookaheadAt(i));
        yield return new TestCaseData("proposer_lookahead", lookahead, sizeof(ulong)).SetName("Proposer_lookahead_ssz");
    }

    /// <summary>A list of fixed-size elements has no offset table: its SSZ is the elements back to back.</summary>
    [TestCaseSource(nameof(SszCases))]
    public async Task Ssz_body_is_the_list_of_fixed_size_elements_with_the_version_header(string endpoint, byte[] expected, int elementSize)
    {
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{StateSlot}/{endpoint}", Octet);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(Octet));
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("fulu"));
        Assert.That(body.Length % elementSize, Is.Zero);
        Assert.That(body, Is.EqualTo(expected));
    }

    [Test]
    public async Task Errors_follow_the_published_responses(
        [Values("pending_deposits", "pending_partial_withdrawals", "pending_consolidations", "proposer_lookahead")] string endpoint,
        [Values("bad_id", "unknown_slot", "unknown_state_root", "not_acceptable")] string scenario)
    {
        (string stateId, string accept, HttpStatusCode expected) = scenario switch
        {
            "bad_id" => ("not-a-state", Json, HttpStatusCode.BadRequest),
            "unknown_slot" => ((StateSlot + 1).ToString(), Json, HttpStatusCode.NotFound),
            "unknown_state_root" => (BeaconApiTestHost.FilledHash(0xee).ToString(), Json, HttpStatusCode.NotFound),
            _ => (StateSlot.ToString(), "text/plain", HttpStatusCode.NotAcceptable),
        };

        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{stateId}/{endpoint}", accept);
        await BeaconApiTestHost.AssertErrorAsync(response, expected);
    }

    /// <summary>
    /// apis/beacon/states/pending_*.yaml and proposer_lookahead.yaml: a state from before the fork that introduced the
    /// field is 400. Only the slot of such a state is ever read, so the stored bytes carry nothing else.
    /// </summary>
    [TestCase("pending_deposits", PreElectraEpoch)]
    [TestCase("pending_partial_withdrawals", PreElectraEpoch)]
    [TestCase("pending_consolidations", PreElectraEpoch)]
    [TestCase("proposer_lookahead", PreElectraEpoch)]
    [TestCase("proposer_lookahead", ElectraEpoch)]
    public async Task State_before_the_field_was_introduced_is_400(string endpoint, ulong epoch)
    {
        ulong slot = epoch * 32;
        Hash256 root = BeaconApiTestHost.TestRoot((byte)(epoch / 100_000));
        byte[] slotOnly = new byte[48];
        BinaryPrimitives.WriteUInt64LittleEndian(slotOnly.AsSpan(40), slot);
        _host.Store.PutState(root, slotOnly);
        _host.Store.PutBlock(root, BeaconApiTestHost.MinimalBlock(slot));
        _host.Store.SetCanonicalRoot(slot, root);

        foreach (string accept in new[] { Json, Octet })
        {
            using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{slot}/{endpoint}", accept);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), accept);
        }
    }

    private static async Task<JsonElement> ReadVersionedEnvelope(HttpResponseMessage response, bool expectedFinalized)
    {
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(Json));
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo("fulu"));

        JsonElement root = JsonDocument.Parse(raw).RootElement;
        Assert.That(root.GetProperty("version").GetString(), Is.EqualTo("fulu"));
        Assert.That(root.GetProperty("execution_optimistic").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("finalized").GetBoolean(), Is.EqualTo(expectedFinalized));
        return root;
    }

    private static string Flatten(JsonElement item) =>
        string.Join(';', item.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}"));

    private static string Hex(int length, byte fill) => BeaconApiTestHost.Hex(length, fill);

    private static ulong LookaheadAt(int i) => (ulong)(1000 + i * 7);

    private static BeaconStateFulu QueueState()
    {
        QueueFixture f = new();
        BeaconStateFulu state = BeaconApiTestHost.RichState(BeaconChainSpec.Mainnet, StateSlot);
        state.LatestBlockHeader!.Slot = StateSlot;
        state.PendingDeposits = f.Deposits;
        state.PendingPartialWithdrawals = f.Withdrawals;
        state.PendingConsolidations = f.Consolidations;
        state.ProposerLookahead = [.. Enumerable.Range(0, 64).Select(LookaheadAt)];
        return state;
    }

    private sealed class QueueFixture
    {
        public PendingDeposit[] Deposits { get; } =
        [
            new PendingDeposit { Pubkey = BeaconApiTestHost.FilledPubkey(0x71), WithdrawalCredentials = BeaconApiTestHost.FilledHash(0x72), Amount = 1_000_000_000, Signature = BeaconApiTestHost.FilledSignature(0x73), Slot = StateSlot - 5 },
            new PendingDeposit { Pubkey = BeaconApiTestHost.FilledPubkey(0x74), WithdrawalCredentials = BeaconApiTestHost.FilledHash(0x75), Amount = 32_000_000_000, Signature = BeaconApiTestHost.FilledSignature(0x76), Slot = StateSlot - 3 },
        ];

        public PendingPartialWithdrawal[] Withdrawals { get; } =
        [
            new PendingPartialWithdrawal { ValidatorIndex = 2, Amount = 1_000, WithdrawableEpoch = 412_700 },
            new PendingPartialWithdrawal { ValidatorIndex = 0, Amount = 7, WithdrawableEpoch = 412_701 },
        ];

        public PendingConsolidation[] Consolidations { get; } =
        [
            new PendingConsolidation { SourceIndex = 0, TargetIndex = 2 },
            new PendingConsolidation { SourceIndex = 1, TargetIndex = 2 },
        ];
    }
}
