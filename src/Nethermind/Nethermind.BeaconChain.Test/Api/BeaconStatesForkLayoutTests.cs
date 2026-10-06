// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.Api;

public class BeaconStatesForkLayoutTests : BeaconApiFixture
{
    private const string Json = "application/json";
    private const string Octet = "application/octet-stream";

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Sepolia;

    // Enough validators that every slot's committee is non-empty, which upgrade_to_gloas needs to fill its PTC window.
    private const int ValidatorCount = 64;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await StartAsync(Spec);
        foreach (string fork in new[] { "electra", "fulu", "gloas" })
        {
            ulong slot = SlotOf(fork);
            Hash256 root = TestRoot((byte)(0xc0 + Marker(fork)));
            _host.Store.PutState(root, EncodedState(fork));
            _host.Store.SetCanonicalRoot(slot, root);
        }
    }

    [TestCase("electra")]
    [TestCase("fulu")]
    [TestCase("gloas")]
    public async Task Pending_queues_are_served_in_the_layout_of_the_state_fork(string fork)
    {
        foreach (string endpoint in new[] { "pending_deposits", "pending_partial_withdrawals", "pending_consolidations" })
        {
            JsonElement root = await ReadVersioned($"/eth/v1/beacon/states/{SlotOf(fork)}/{endpoint}", fork);
            JsonElement entry = root.GetProperty("data").EnumerateArray().Single();
            string expected = endpoint switch
            {
                "pending_deposits" => (StateSlot(fork) - (ulong)Marker(fork)).ToString(),
                "pending_partial_withdrawals" => (1000 + Marker(fork)).ToString(),
                _ => Marker(fork).ToString(),
            };
            string actual = entry.GetProperty(endpoint switch { "pending_deposits" => "slot", "pending_partial_withdrawals" => "amount", _ => "source_index" }).GetString()!;
            Assert.That(actual, Is.EqualTo(expected), endpoint);
        }
    }

    [TestCase("fulu")]
    [TestCase("gloas")]
    public async Task Proposer_lookahead_is_served_from_fulu_on(string fork)
    {
        JsonElement root = await ReadVersioned($"/eth/v1/beacon/states/{SlotOf(fork)}/proposer_lookahead", fork);
        Assert.That(root.GetProperty("data").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(Enumerable.Range(0, 64).Select(i => LookaheadAt(fork, i).ToString())));

        using HttpResponseMessage ssz = await _host.GetAsync($"/eth/v1/beacon/states/{SlotOf(fork)}/proposer_lookahead", Octet);
        Assert.That(ssz.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo(fork));
        Assert.That((await ssz.Content.ReadAsByteArrayAsync()).Length, Is.EqualTo(64 * sizeof(ulong)));
    }

    [Test]
    public async Task Proposer_lookahead_of_an_electra_state_is_400()
    {
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{SlotOf("electra")}/proposer_lookahead", Json);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Unversioned_state_endpoints_read_each_fork_layout(
        [Values("electra", "fulu", "gloas")] string fork,
        [Values("fork", "finality_checkpoints", "validators", "validator_balances", "committees", "randao", "sync_committees")] string endpoint)
    {
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{SlotOf(fork)}/{endpoint}", Json);
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        JsonElement data = JsonDocument.Parse(raw).RootElement.GetProperty("data");

        switch (endpoint)
        {
            case "fork":
                Assert.That(data.GetProperty("current_version").GetString(), Is.EqualTo(fork == "gloas" ? "0x90000076" : $"0x0{Marker(fork)}000000"));
                break;
            case "finality_checkpoints":
                Assert.That(data.GetProperty("finalized").GetProperty("epoch").GetString(), Is.EqualTo((100 + Marker(fork)).ToString()));
                break;
            case "validators":
            case "validator_balances":
                Assert.That(data.EnumerateArray().Select(e => e.GetProperty("balance").GetString()), Is.EqualTo(Enumerable.Range(0, ValidatorCount).Select(i => BalanceOf(fork, i).ToString())));
                break;
            case "committees":
                Assert.That(data.EnumerateArray().SelectMany(e => e.GetProperty("validators").EnumerateArray()).Select(e => e.GetString()), Is.EquivalentTo(Enumerable.Range(0, ValidatorCount).Select(i => i.ToString())));
                break;
            case "randao":
                Assert.That(data.GetProperty("randao").GetString(), Is.EqualTo(FilledHash((byte)(0xd0 + Marker(fork))).ToString()));
                break;
            default:
                Assert.That(data.GetProperty("validators").EnumerateArray().Select(e => e.GetString()).Distinct(), Is.EquivalentTo(new[] { "0", "1", "2" }));
                break;
        }
    }

    [Test]
    public async Task Validator_identities_read_each_fork_layout([Values("electra", "fulu", "gloas")] string fork)
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"/eth/v1/beacon/states/{SlotOf(fork)}/validator_identities") { Content = new StringContent("[\"2\"]", Encoding.UTF8, Json) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(Json));
        using HttpResponseMessage response = await _host.Client.SendAsync(request);
        JsonElement entry = (await ReadJsonAsync(response)).RootElement.GetProperty("data").EnumerateArray().Single();
        Assert.That(entry.GetProperty("activation_epoch").GetString(), Is.EqualTo("5"));
    }

    [TestCase("electra", false)]
    [TestCase("fulu", true)]
    [TestCase("gloas", true)]
    public async Task Debug_state_json_follows_the_fork_layout(string fork, bool hasLookahead)
    {
        JsonElement root = await ReadVersioned($"/eth/v2/debug/beacon/states/{SlotOf(fork)}", fork);
        JsonElement data = root.GetProperty("data");
        Assert.That(data.TryGetProperty("proposer_lookahead", out _), Is.EqualTo(hasLookahead));
        Assert.That(data.GetProperty("pending_consolidations")[0].GetProperty("source_index").GetString(), Is.EqualTo(Marker(fork).ToString()));
    }

    [Test]
    public async Task Debug_state_of_gloas_serves_builder_and_ptc_fields()
    {
        JsonElement data = (await ReadVersioned($"/eth/v2/debug/beacon/states/{SlotOf("gloas")}", "gloas")).GetProperty("data");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(data.EnumerateObject().Count(), Is.EqualTo(46));
            Assert.That(data.TryGetProperty("latest_execution_payload_header", out _), Is.False);
            Assert.That(data.GetProperty("builders")[0].GetProperty("version").GetString(), Is.EqualTo("255"));
            Assert.That(data.GetProperty("builders")[0].GetProperty("balance").GetString(), Is.EqualTo(ulong.MaxValue.ToString()));
            Assert.That(data.GetProperty("builder_pending_payments")[0].GetProperty("withdrawal").GetProperty("amount").GetString(), Is.EqualTo("123"));
            Assert.That(data.GetProperty("builder_pending_withdrawals")[0].GetProperty("builder_index").GetString(), Is.EqualTo("9"));
            Assert.That(data.GetProperty("payload_expected_withdrawals")[0].GetProperty("amount").GetString(), Is.EqualTo("456"));
            Assert.That(data.GetProperty("execution_payload_availability").GetString(), Is.EqualTo("0xfe" + new string('f', 2046)));
            Assert.That(data.GetProperty("ptc_window").GetArrayLength(), Is.EqualTo(96));
            Assert.That(data.GetProperty("ptc_window")[0].GetArrayLength(), Is.EqualTo(512));
            Assert.That(data.GetProperty("ptc_window")[0][0].GetString(), Is.EqualTo(ulong.MaxValue.ToString()));
            Assert.That(data.GetProperty("current_epoch_participation")[0].GetString(), Is.EqualTo("7"));
        }

        using HttpResponseMessage ssz = await _host.GetAsync($"/eth/v2/debug/beacon/states/{SlotOf("gloas")}", Octet);
        Assert.That(ssz.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await ssz.Content.ReadAsByteArrayAsync(), Is.EqualTo(EncodedState("gloas")));
    }

    private async Task<JsonElement> ReadVersioned(string path, string fork)
    {
        using HttpResponseMessage response = await _host.GetAsync(path, Json);
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw.Length > 500 ? raw[..500] : raw);
        Assert.That(response.Headers.GetValues("Eth-Consensus-Version").Single(), Is.EqualTo(fork));
        JsonElement root = JsonDocument.Parse(raw).RootElement;
        Assert.That(root.GetProperty("version").GetString(), Is.EqualTo(fork));
        return root;
    }

    private static int Marker(string fork) => fork switch { "electra" => 5, "fulu" => 6, _ => 7 };

    private static ulong StateEpoch(string fork) => fork switch
    {
        "electra" => Spec.ElectraForkEpoch + 10,
        "fulu" => Spec.FuluForkEpoch + 10,
        _ => Spec.GloasForkEpoch,
    };

    private static ulong StateSlot(string fork) => StateEpoch(fork) * 32;
    private static ulong SlotOf(string fork) => StateSlot(fork);
    private static ulong BalanceOf(string fork, int i) => 32_000_000_000 + (ulong)(Marker(fork) * 10 + i);
    private static ulong LookaheadAt(string fork, int i) => (ulong)((i + Marker(fork)) % 3);

    private static byte[] EncodedState(string fork)
    {
        ulong slot = StateSlot(fork);
        int marker = Marker(fork);
        BeaconStateFulu state = RichState(Spec, slot);
        state.Fork = new Fork { PreviousVersion = [(byte)(marker - 1), 0, 0, 0], CurrentVersion = [(byte)marker, 0, 0, 0], Epoch = StateEpoch(fork) };
        state.LatestBlockHeader!.Slot = slot;
        Validator first = state.Validators![0];
        state.Validators = [.. state.Validators, .. Enumerable.Range(3, ValidatorCount - 3).Select(i =>
        {
            Validator validator = first.Clone();
            validator.Pubkey = new BlsPublicKey([0xe0, (byte)i, .. new byte[46]]);
            return validator;
        })];
        state.Balances = [.. Enumerable.Range(0, ValidatorCount).Select(i => BalanceOf(fork, i))];
        state.PreviousEpochParticipation = new byte[ValidatorCount];
        state.CurrentEpochParticipation = new byte[ValidatorCount];
        state.InactivityScores = new ulong[ValidatorCount];
        state.FinalizedCheckpoint = new Checkpoint { Epoch = (ulong)(100 + marker), Root = FilledHash(0x33) };
        state.RandaoMixes![(int)(StateEpoch(fork) % 65_536)] = FilledHash((byte)(0xd0 + marker));
        state.PendingDeposits![0].Slot = slot - (ulong)marker;
        state.PendingPartialWithdrawals![0].Amount = (ulong)(1000 + marker);
        state.PendingConsolidations![0].SourceIndex = (ulong)marker;
        state.ProposerLookahead = [.. Enumerable.Range(0, 64).Select(i => LookaheadAt(fork, i))];
        BlsPublicKey[] members = [.. Enumerable.Range(0, 512).Select(i => state.Validators![i % 3].Pubkey)];
        state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = members, AggregatePubkey = FilledPubkey(0x47) };
        state.NextSyncCommittee = new SyncCommittee { Pubkeys = members, AggregatePubkey = FilledPubkey(0x48) };

        if (fork == "electra") return BeaconStateElectra.Encode(state);
        if (fork == "fulu") return BeaconStateFulu.Encode(state);
        BeaconStateGloas gloas = GloasForkTransition.UpgradeToGloas(state, Spec);
        gloas.Builders = [new Builder { Pubkey = FilledPubkey(0x49), Version = 255, ExecutionAddress = Nethermind.Core.Address.Zero, Balance = ulong.MaxValue }];
        gloas.BuilderPendingPayments![0].Withdrawal!.Amount = 123;
        gloas.BuilderPendingWithdrawals = [new BuilderPendingWithdrawal { FeeRecipient = Nethermind.Core.Address.Zero, BuilderIndex = 9 }];
        gloas.PayloadExpectedWithdrawals = [new() { Address = Nethermind.Core.Address.Zero, Amount = 456 }];
        gloas.ExecutionPayloadAvailability![0] = false;
        gloas.PtcWindow![0].Indices = [ulong.MaxValue, .. new ulong[511]];
        gloas.CurrentEpochParticipation![0] = 7;
        return BeaconStateGloas.Encode(gloas);
    }
}
