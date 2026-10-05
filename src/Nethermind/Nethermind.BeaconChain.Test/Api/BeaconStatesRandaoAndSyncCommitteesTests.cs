// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Http;
using System.Text.Json;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.Api;

public class BeaconStatesRandaoAndSyncCommitteesTests : BeaconApiFixture
{
    private const string Json = "application/json";

    // Mainnet Fulu epoch inside sync committee period 1611, which covers epochs 412,416 to 412,671.
    private const ulong StateEpoch = 412_500;
    private const ulong StateSlot = StateEpoch * 32;
    private const ulong PeriodStart = 412_416;
    private const ulong NextPeriodStart = PeriodStart + 256;
    private const ulong OldestMixEpoch = StateEpoch - 65_535;
    private const ulong CorruptSlot = StateSlot + 32;

    private static readonly Hash256 StateRoot = BeaconApiTestHost.TestRoot(0x70);
    private static readonly Hash256 CorruptRoot = BeaconApiTestHost.TestRoot(0x71);

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet);
        Put(StateRoot, StateSlot, FixtureState(StateSlot));

        BeaconStateFulu corrupt = FixtureState(CorruptSlot);
        corrupt.CurrentSyncCommittee!.Pubkeys![300] = BeaconApiTestHost.FilledPubkey(0xee);
        Put(CorruptRoot, CorruptSlot, corrupt);
    }

    [SetUp]
    public void ResetSharedState() => _host.SetStatus(StateRoot, Hash256.Zero, 0);

    [TestCase(null, 0xa0)]
    [TestCase(StateEpoch, 0xa0)]
    [TestCase(StateEpoch - 10, 0xa1)]
    [TestCase(OldestMixEpoch, 0xa2)]
    public async Task Randao_returns_the_mix_recorded_for_the_requested_epoch(ulong? epoch, int fill)
    {
        string query = epoch is null ? "" : $"?epoch={epoch}";
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{StateSlot}/randao{query}", Json);
        JsonElement data = await ReadEnvelope(response, expectedFinalized: false);
        Assert.That(data.GetProperty("randao").GetString(), Is.EqualTo(BeaconApiTestHost.FilledHash((byte)fill).ToString()));
    }

    [TestCase("randao", "412501")] // StateEpoch + 1
    [TestCase("randao", "346964")] // OldestMixEpoch - 1
    [TestCase("randao", "not-an-epoch")]
    [TestCase("sync_committees", "412415")] // PeriodStart - 1
    [TestCase("sync_committees", "412928")] // NextPeriodStart + 256
    [TestCase("sync_committees", "0")]
    [TestCase("sync_committees", "-1")]
    public async Task Epoch_the_state_cannot_answer_or_malformed_is_400(string endpoint, string epoch)
    {
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{StateSlot}/{endpoint}?epoch={epoch}", Json);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>
    /// An epoch after the state's is 400 even where get_randao_mix's unsigned age would wrap into the window,
    /// which needs a Fulu state younger than EPOCHS_PER_HISTORICAL_VECTOR: Hoodi epoch 60,000 (Fulu from 50,688).
    /// </summary>
    [TestCase("60000", HttpStatusCode.OK)]
    [TestCase("60001", HttpStatusCode.BadRequest)]
    [TestCase("18446744073709551615", HttpStatusCode.BadRequest)]
    public async Task Randao_epoch_after_the_state_is_400_even_when_the_age_wraps(string epoch, HttpStatusCode expected)
    {
        const ulong hoodiSlot = 60_000UL * 32;
        await using BeaconApiTestHost hoodi = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Hoodi);
        BeaconStateFulu state = BeaconApiTestHost.RichState(BeaconChainSpec.Hoodi, hoodiSlot);
        state.LatestBlockHeader!.Slot = hoodiSlot;
        Hash256 root = BeaconApiTestHost.TestRoot(0x72);
        hoodi.Store.PutState(root, BeaconStateFulu.Encode(state));
        hoodi.Store.PutBlock(root, BeaconApiTestHost.MinimalBlock(hoodiSlot));
        hoodi.Store.SetCanonicalRoot(hoodiSlot, root);

        using HttpResponseMessage response = await hoodi.GetAsync($"/eth/v1/beacon/states/{hoodiSlot}/randao?epoch={epoch}", Json);
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [TestCase(null, false)]
    [TestCase(PeriodStart, false)]
    [TestCase(NextPeriodStart - 1, false)]
    [TestCase(NextPeriodStart, true)]
    [TestCase(NextPeriodStart + 255, true)]
    public async Task Sync_committee_maps_each_position_to_its_registry_index_and_slices_four_subnets(ulong? epoch, bool next)
    {
        string query = epoch is null ? "" : $"?epoch={epoch}";
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{StateSlot}/sync_committees{query}", Json);
        JsonElement data = await ReadEnvelope(response, expectedFinalized: false);

        string[] expected = [.. Enumerable.Range(0, 512).Select(i => MemberAt(i, next).ToString())];
        string[] validators = [.. data.GetProperty("validators").EnumerateArray().Select(e => e.GetString()!)];
        Assert.That(validators, Is.EqualTo(expected));

        JsonElement aggregates = data.GetProperty("validator_aggregates");
        Assert.That(aggregates.GetArrayLength(), Is.EqualTo(4));
        for (int subnet = 0; subnet < 4; subnet++)
        {
            string[] slice = [.. aggregates[subnet].EnumerateArray().Select(e => e.GetString()!)];
            Assert.That(slice, Is.EqualTo(expected.Skip(subnet * 128).Take(128).ToArray()), $"subnet {subnet}");
        }
    }

    [Test]
    public async Task Sync_committee_member_missing_from_the_registry_is_500()
    {
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{CorruptSlot}/sync_committees", Json);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
    }

    [Test]
    public async Task Finalized_flag_follows_the_finalized_checkpoint([Values("randao", "sync_committees")] string endpoint)
    {
        _host.SetStatus(StateRoot, StateRoot, StateEpoch);
        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{StateSlot}/{endpoint}", Json);
        await ReadEnvelope(response, expectedFinalized: true);
    }

    [Test]
    public async Task Errors_follow_the_published_responses(
        [Values("randao", "sync_committees")] string endpoint,
        [Values("bad_id", "unknown_slot", "unknown_state_root", "not_acceptable")] string scenario)
    {
        (string stateId, string accept, HttpStatusCode expected) = scenario switch
        {
            "bad_id" => ("not-a-state", Json, HttpStatusCode.BadRequest),
            "unknown_slot" => ((StateSlot + 1).ToString(), Json, HttpStatusCode.NotFound),
            "unknown_state_root" => (BeaconApiTestHost.FilledHash(0xee).ToString(), Json, HttpStatusCode.NotFound),
            _ => (StateSlot.ToString(), "application/octet-stream", HttpStatusCode.NotAcceptable),
        };

        using HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/states/{stateId}/{endpoint}", accept);
        await BeaconApiTestHost.AssertErrorAsync(response, expected);
    }

    private static async Task<JsonElement> ReadEnvelope(HttpResponseMessage response, bool expectedFinalized)
    {
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(Json));

        JsonElement root = JsonDocument.Parse(raw).RootElement;
        Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "execution_optimistic", "finalized", "data" }));
        Assert.That(root.GetProperty("execution_optimistic").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("finalized").GetBoolean(), Is.EqualTo(expectedFinalized));
        return root.GetProperty("data");
    }

    /// <summary>Registry index at committee position <paramref name="i"/>: the current committee repeats members, the next one is grouped by subnet.</summary>
    private static int MemberAt(int i, bool next) => next ? (i / 128) % 3 : (i * 7 + i / 5) % 3;

    private static BeaconStateFulu FixtureState(ulong slot)
    {
        BeaconStateFulu state = BeaconApiTestHost.RichState(BeaconChainSpec.Mainnet, slot);
        state.LatestBlockHeader!.Slot = slot;

        Validator[] registry = state.Validators!;
        state.CurrentSyncCommittee = new SyncCommittee
        {
            Pubkeys = [.. Enumerable.Range(0, 512).Select(i => registry[MemberAt(i, next: false)].Pubkey)],
            AggregatePubkey = BeaconApiTestHost.FilledPubkey(0x47),
        };
        state.NextSyncCommittee = new SyncCommittee
        {
            Pubkeys = [.. Enumerable.Range(0, 512).Select(i => registry[MemberAt(i, next: true)].Pubkey)],
            AggregatePubkey = BeaconApiTestHost.FilledPubkey(0x48),
        };

        const ulong vector = 65_536;
        state.RandaoMixes![(int)(StateEpoch % vector)] = BeaconApiTestHost.FilledHash(0xa0);
        state.RandaoMixes[(int)((StateEpoch - 10) % vector)] = BeaconApiTestHost.FilledHash(0xa1);
        state.RandaoMixes[(int)(OldestMixEpoch % vector)] = BeaconApiTestHost.FilledHash(0xa2);
        return state;
    }

    private void Put(Hash256 root, ulong slot, BeaconStateFulu state)
    {
        _host.Store.PutState(root, BeaconStateFulu.Encode(state));
        _host.Store.PutBlock(root, BeaconApiTestHost.MinimalBlock(slot));
        _host.Store.SetCanonicalRoot(slot, root);
    }
}
