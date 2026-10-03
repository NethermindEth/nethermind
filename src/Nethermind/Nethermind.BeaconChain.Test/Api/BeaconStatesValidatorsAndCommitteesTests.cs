// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// <c>/eth/v1/beacon/states/{state_id}/validators*</c> and <c>/committees</c>: every assertion here
/// compares against a status or committee membership derived independently of
/// <c>ValidatorStatus.Classify</c>/<c>CommitteeCache</c> - by construction of the fixture, not by
/// re-running the production code - so a wrong classification or a broken shuffling partition would
/// actually fail these.
/// </summary>
public class BeaconStatesValidatorsAndCommitteesTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    // epoch 412,500: past FuluForkEpoch (411,392) so BeaconStateCodec accepts it, and Presets.SlotsPerEpoch-aligned.
    private const ulong StateEpoch = 412_500;
    private const ulong StateSlot = StateEpoch * 32;

    private BeaconApiTestHost _host = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await BeaconApiTestHost.StartAsync(Spec, forkAwareStore: false);
        _host.Client.Timeout = TimeSpan.FromSeconds(5);
    }

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    /// <summary>
    /// One validator per beacon-api status string, each constructed so only one branch of
    /// get_validator_status's rules can fire; the expected label is the test author's own reading
    /// of that spec, written down before the request is made, not a value read off the response.
    /// </summary>
    private static readonly (string ExpectedStatus, Validator Validator)[] StatusFixture =
    [
        ("pending_initialized", MakeValidator(activationEligibility: Presets.FarFutureEpoch, activation: Presets.FarFutureEpoch, exit: Presets.FarFutureEpoch, withdrawable: Presets.FarFutureEpoch, slashed: false, effectiveBalance: 32_000_000_000)),
        ("pending_queued", MakeValidator(activationEligibility: StateEpoch - 10, activation: StateEpoch + 100, exit: Presets.FarFutureEpoch, withdrawable: Presets.FarFutureEpoch, slashed: false, effectiveBalance: 32_000_000_000)),
        ("active_ongoing", MakeValidator(activationEligibility: 0, activation: StateEpoch - 100, exit: Presets.FarFutureEpoch, withdrawable: Presets.FarFutureEpoch, slashed: false, effectiveBalance: 32_000_000_000)),
        ("active_exiting", MakeValidator(activationEligibility: 0, activation: StateEpoch - 100, exit: StateEpoch + 50, withdrawable: Presets.FarFutureEpoch, slashed: false, effectiveBalance: 32_000_000_000)),
        ("active_slashed", MakeValidator(activationEligibility: 0, activation: StateEpoch - 100, exit: StateEpoch + 50, withdrawable: Presets.FarFutureEpoch, slashed: true, effectiveBalance: 32_000_000_000)),
        ("exited_unslashed", MakeValidator(activationEligibility: 0, activation: StateEpoch - 200, exit: StateEpoch - 10, withdrawable: StateEpoch + 50, slashed: false, effectiveBalance: 32_000_000_000)),
        ("exited_slashed", MakeValidator(activationEligibility: 0, activation: StateEpoch - 200, exit: StateEpoch - 10, withdrawable: StateEpoch + 50, slashed: true, effectiveBalance: 32_000_000_000)),
        ("withdrawal_possible", MakeValidator(activationEligibility: 0, activation: StateEpoch - 200, exit: StateEpoch - 100, withdrawable: StateEpoch - 5, slashed: false, effectiveBalance: 32_000_000_000)),
        ("withdrawal_done", MakeValidator(activationEligibility: 0, activation: StateEpoch - 200, exit: StateEpoch - 100, withdrawable: StateEpoch - 5, slashed: false, effectiveBalance: 0)),
    ];

    [Test]
    public async Task Validators_list_classifies_every_beacon_api_status_correctly()
    {
        Hash256 root = TestRoot(10);
        Validator[] validators = [.. StatusFixture.Select(f => f.Validator)];
        ulong[] balances = [.. validators.Select(v => v.EffectiveBalance)];
        PutState(root, validators, balances);

        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators");
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"body: {raw}");
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));

        JsonDocument body = JsonDocument.Parse(raw);
        JsonElement data = body.RootElement.GetProperty("data");
        Assert.That(data.GetArrayLength(), Is.EqualTo(StatusFixture.Length));

        for (int i = 0; i < StatusFixture.Length; i++)
        {
            JsonElement entry = data[i];
            Assert.That(entry.GetProperty("index").GetString(), Is.EqualTo(i.ToString()));
            Assert.That(entry.GetProperty("status").GetString(), Is.EqualTo(StatusFixture[i].ExpectedStatus),
                $"validator {i} ({StatusFixture[i].ExpectedStatus}) misclassified");
            Assert.That(entry.GetProperty("validator").GetProperty("pubkey").GetString(),
                Is.EqualTo(StatusFixture[i].Validator.Pubkey.ToString()));
        }
    }

    [TestCase("active", new[] { "active_ongoing", "active_exiting", "active_slashed" })]
    [TestCase("exited", new[] { "exited_unslashed", "exited_slashed" })]
    [TestCase("active_ongoing", new[] { "active_ongoing" })]
    public async Task Validators_list_status_filter_matches_the_broad_group_or_the_exact_status(string filter, string[] expectedStatuses)
    {
        Hash256 root = TestRoot(11);
        Validator[] validators = [.. StatusFixture.Select(f => f.Validator)];
        ulong[] balances = [.. validators.Select(v => v.EffectiveBalance)];
        PutState(root, validators, balances);

        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators?status={filter}");
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = body.RootElement.GetProperty("data");

        List<string> statuses = [.. data.EnumerateArray().Select(e => e.GetProperty("status").GetString()!)];
        Assert.That(statuses, Is.EquivalentTo(expectedStatuses));
    }

    [Test]
    public async Task Validators_list_id_filter_accepts_index_and_pubkey_and_rejects_garbage()
    {
        Hash256 root = TestRoot(12);
        Validator[] validators = [.. StatusFixture.Select(f => f.Validator)];
        ulong[] balances = [.. validators.Select(v => v.EffectiveBalance)];
        PutState(root, validators, balances);

        string pubkeyOfIndex2 = validators[2].Pubkey.ToString();
        HttpResponseMessage byMixedId = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators?id=0&id={pubkeyOfIndex2}");
        JsonDocument mixedBody = JsonDocument.Parse(await byMixedId.Content.ReadAsStringAsync());
        List<string> indices = [.. mixedBody.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("index").GetString()!)];
        Assert.That(indices, Is.EquivalentTo(new[] { "0", "2" }));

        HttpResponseMessage badId = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators?id=not-a-real-id");
        Assert.That(badId.StatusCode, Is.EqualTo((HttpStatusCode)400));
    }

    /// <summary>
    /// A stale or misbuilt per-request pubkey map would resolve the second pubkey id to the wrong
    /// index (or fail to find it) once the map holds more than one entry - a single-pubkey-id test
    /// cannot distinguish a correct map from one that only works for its first insertion.
    /// </summary>
    [Test]
    public async Task Validators_list_resolves_multiple_pubkey_ids_through_the_shared_request_map()
    {
        Hash256 root = TestRoot(20);
        Validator[] validators = [.. StatusFixture.Select(f => f.Validator)];
        ulong[] balances = [.. validators.Select(v => v.EffectiveBalance)];
        PutState(root, validators, balances);

        string pubkeyOfIndex1 = validators[1].Pubkey.ToString();
        string pubkeyOfIndex4 = validators[4].Pubkey.ToString();
        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators?id={pubkeyOfIndex1}&id={pubkeyOfIndex4}");
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        List<string> indices = [.. body.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("index").GetString()!)];
        Assert.That(indices, Is.EquivalentTo(new[] { "1", "4" }));
    }

    /// <summary>
    /// <c>validator_balances</c> had no id-filter coverage at all before this: this both proves the
    /// filter works there and, with two pubkey ids, exercises the same shared per-request map as the
    /// <c>validators</c> list test above.
    /// </summary>
    [Test]
    public async Task Validator_balances_id_filter_accepts_index_and_multiple_pubkey_ids()
    {
        Hash256 root = TestRoot(21);
        Validator[] validators = [.. StatusFixture.Select(f => f.Validator)];
        ulong[] balances = [.. validators.Select(v => v.EffectiveBalance)];
        PutState(root, validators, balances);

        string pubkeyOfIndex1 = validators[1].Pubkey.ToString();
        string pubkeyOfIndex4 = validators[4].Pubkey.ToString();
        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validator_balances?id=0&id={pubkeyOfIndex1}&id={pubkeyOfIndex4}");
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        List<string> indices = [.. body.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("index").GetString()!)];
        Assert.That(indices, Is.EquivalentTo(new[] { "0", "1", "4" }));
    }

    /// <summary>
    /// The beacon-api spec declares both <c>id</c> and <c>status</c> as array-typed query
    /// parameters; the repeated-key form (<c>id=a&amp;id=b</c>) is the array's canonical wire
    /// encoding, and comma-joined is a common client shorthand this driver also accepts. Only the
    /// mixed-id, repeated-key case had a test before this one - this proves comma-joining alone,
    /// and repetition of a comma-joined status filter, independently of that existing case.
    /// </summary>
    [Test]
    public async Task Validators_list_id_and_status_filters_accept_both_comma_joined_and_repeated_forms()
    {
        Hash256 root = TestRoot(18);
        Validator[] validators = [.. StatusFixture.Select(f => f.Validator)];
        ulong[] balances = [.. validators.Select(v => v.EffectiveBalance)];
        PutState(root, validators, balances);

        // id=0,2 (comma-joined) must select the same two validators as the repeated-key form does.
        HttpResponseMessage commaId = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators?id=0,2");
        JsonDocument commaIdBody = JsonDocument.Parse(await commaId.Content.ReadAsStringAsync());
        List<string> commaIndices = [.. commaIdBody.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("index").GetString()!)];
        Assert.That(commaIndices, Is.EquivalentTo(new[] { "0", "2" }), "comma-joined id list must be split, not treated as one unmatched id");

        // status=active_ongoing&status=exited_slashed (repeated key, not comma) must union both groups.
        HttpResponseMessage repeatedStatus = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators?status=active_ongoing&status=exited_slashed");
        JsonDocument repeatedStatusBody = JsonDocument.Parse(await repeatedStatus.Content.ReadAsStringAsync());
        List<string> statuses = [.. repeatedStatusBody.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("status").GetString()!)];
        Assert.That(statuses, Is.EquivalentTo(new[] { "active_ongoing", "exited_slashed" }),
            "a repeated status= key is the array parameter's canonical form and must union, not overwrite or reject");
    }

    /// <summary>
    /// Unlike <c>id</c> and <c>status</c>, the committees endpoint's <c>index</c> parameter is
    /// declared as a single <c>Uint64</c> in the beacon-api spec, not an array. Sending it twice is
    /// caller error, not a filter to union - this driver rejects the ambiguity as 400 rather than
    /// silently picking one occurrence, which would be confidently wrong the other half of the time.
    /// </summary>
    [Test]
    public async Task Committees_repeated_index_key_is_400_not_a_silently_picked_value()
    {
        const int validatorCount = 50;
        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            validators[i] = MakeValidator(0, 0, Presets.FarFutureEpoch, Presets.FarFutureEpoch, false, 32_000_000_000);
            balances[i] = 32_000_000_000;
        }

        Hash256 root = TestRoot(19);
        PutState(root, validators, balances);

        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/committees?index=0&index=1");
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)400),
            "index is a single Uint64 per the beacon-api spec; two occurrences is ambiguous input, not a two-element filter");
    }

    [Test]
    public async Task ValidatorById_is_404_for_an_out_of_range_index_and_400_for_a_malformed_id()
    {
        Hash256 root = TestRoot(13);
        Validator[] validators = [.. StatusFixture.Select(f => f.Validator)];
        ulong[] balances = [.. validators.Select(v => v.EffectiveBalance)];
        PutState(root, validators, balances);

        HttpResponseMessage outOfRange = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators/{validators.Length + 5}");
        Assert.That(outOfRange.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        HttpResponseMessage malformed = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators/0xnotapubkey");
        Assert.That(malformed.StatusCode, Is.EqualTo((HttpStatusCode)400));

        HttpResponseMessage ok = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators/3");
        JsonDocument body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("data").GetProperty("status").GetString(), Is.EqualTo("active_exiting"));
    }

    [Test]
    public async Task Validator_balances_reports_the_states_own_balance_array_not_effective_balance()
    {
        Hash256 root = TestRoot(14);
        Validator[] validators = [MakeValidator(0, 0, Presets.FarFutureEpoch, Presets.FarFutureEpoch, false, 32_000_000_000)];
        // A different value from effective_balance so the test cannot pass by accidentally reading the wrong field.
        ulong[] balances = [32_100_000_000];
        PutState(root, validators, balances);

        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validator_balances");
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement entry = body.RootElement.GetProperty("data")[0];
        Assert.That(entry.GetProperty("balance").GetString(), Is.EqualTo("32100000000"));
        Assert.That(entry.GetProperty("index").GetString(), Is.EqualTo("0"));
    }

    [Test]
    public async Task Committees_partition_every_active_validator_exactly_once_across_the_epoch()
    {
        const int validatorCount = 50;
        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            validators[i] = MakeValidator(0, 0, Presets.FarFutureEpoch, Presets.FarFutureEpoch, false, 32_000_000_000);
            balances[i] = 32_000_000_000;
        }

        Hash256 root = TestRoot(15);
        PutState(root, validators, balances);

        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/committees?epoch={StateEpoch}");
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"body: {raw}");

        JsonDocument body = JsonDocument.Parse(raw);
        List<int> allMembers = [];
        int distinctSlots = 0;
        HashSet<string> seenSlots = [];
        foreach (JsonElement committee in body.RootElement.GetProperty("data").EnumerateArray())
        {
            seenSlots.Add(committee.GetProperty("slot").GetString()!);
            foreach (JsonElement member in committee.GetProperty("validators").EnumerateArray())
            {
                allMembers.Add(int.Parse(member.GetString()!));
            }
        }

        distinctSlots = seenSlots.Count;
        // Every active validator appears in exactly one committee across the whole epoch: this is
        // an invariant of compute_committee independent of how CommitteeCache implements shuffling.
        Assert.That(allMembers.Distinct().Count(), Is.EqualTo(validatorCount), "every validator must appear exactly once");
        Assert.That(allMembers, Has.Count.EqualTo(validatorCount), "no validator may be duplicated across committees");
        Assert.That(distinctSlots, Is.EqualTo(32), "one committee-index-0 entry for every slot in the epoch (32 slots), given 50 active validators clamps to 1 committee/slot");
    }

    [Test]
    public async Task Committees_epoch_far_outside_the_state_is_400_not_a_stale_shuffling()
    {
        Hash256 root = TestRoot(16);
        Validator[] validators = [MakeValidator(0, 0, Presets.FarFutureEpoch, Presets.FarFutureEpoch, false, 32_000_000_000)];
        ulong[] balances = [32_000_000_000];
        PutState(root, validators, balances);

        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/committees?epoch={StateEpoch + 1000}");
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)400));
    }

    [Test]
    public async Task Committees_filters_by_slot_and_index()
    {
        const int validatorCount = 50;
        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            validators[i] = MakeValidator(0, 0, Presets.FarFutureEpoch, Presets.FarFutureEpoch, false, 32_000_000_000);
            balances[i] = 32_000_000_000;
        }

        Hash256 root = TestRoot(17);
        PutState(root, validators, balances);

        ulong targetSlot = StateSlot + 5;
        HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/committees?slot={targetSlot}&index=0");
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = body.RootElement.GetProperty("data");

        Assert.That(data.GetArrayLength(), Is.EqualTo(1));
        Assert.That(data[0].GetProperty("slot").GetString(), Is.EqualTo(targetSlot.ToString()));
        Assert.That(data[0].GetProperty("index").GetString(), Is.EqualTo("0"));
    }

    /// <summary>apis/beacon/states/validators.yaml rejects unknown status filters with 400.</summary>
    [Test]
    public async Task Unknown_validator_status_is_rejected([Values("bogus", "active_unknown", "ACTIVE")] string status)
    {
        Hash256 root = TestRoot(30);
        PutState(root, [StatusFixture[0].Validator], [32_000_000_000]);
        using HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validators?status={status}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>apis/beacon/states/validators.yaml and validator_balances.yaml limit GET requests to 64 IDs.</summary>
    [Test]
    public async Task Validator_id_limit_precedes_state_resolution(
        [Values("validators", "validator_balances")] string endpoint,
        [Values] bool commaSeparated,
        [Values(64, 65)] int count)
    {
        string ids = string.Join(commaSeparated ? "," : "&id=", Enumerable.Range(0, count));
        using HttpResponseMessage response = await _host.Client.GetAsync($"/eth/v1/beacon/states/invalid/{endpoint}?id={ids}");
        Assert.That((int)response.StatusCode, Is.EqualTo(count > 64 ? 414 : 400));
    }

    private void PutState(Hash256 root, Validator[] validators, ulong[] balances)
    {
        Hash256[] randaoMixes = new Hash256[(int)Presets.EpochsPerHistoricalVector];
        Array.Fill(randaoMixes, TestHash(0x42));

        BeaconStateFulu state = MinimalState(Spec, StateSlot,
            new Fork { PreviousVersion = [5, 0, 0, 0], CurrentVersion = [6, 0, 0, 0], Epoch = StateEpoch },
            validators, balances, randaoMixes);

        byte[] ssz = BeaconStateFulu.Encode(state);
        _host.Store.PutState(root, ssz);
        _host.Store.PutBlock(root, BeaconApiTestHost.MinimalBlock(StateSlot));
        _host.Store.SetCanonicalRoot(StateSlot, root);
    }

    private static int _pubkeyCounter;

    private static Validator MakeValidator(ulong activationEligibility, ulong activation, ulong exit, ulong withdrawable, bool slashed, ulong effectiveBalance)
    {
        int marker = ++_pubkeyCounter;
        byte[] pubkeyBytes = new byte[48];
        pubkeyBytes[0] = (byte)marker;
        pubkeyBytes[1] = (byte)(marker >> 8);

        return new Validator
        {
            Pubkey = new BlsPublicKey(pubkeyBytes),
            WithdrawalCredentials = TestHash((byte)marker),
            EffectiveBalance = effectiveBalance,
            Slashed = slashed,
            ActivationEligibilityEpoch = activationEligibility,
            ActivationEpoch = activation,
            ExitEpoch = exit,
            WithdrawableEpoch = withdrawable,
        };
    }

    private static Hash256 TestHash(byte marker)
    {
        byte[] bytes = new byte[32];
        bytes[0] = marker;
        return new Hash256(bytes);
    }
}
