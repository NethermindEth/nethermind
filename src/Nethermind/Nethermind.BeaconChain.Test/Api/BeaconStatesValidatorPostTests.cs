// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// beacon-APIs v5.0.0-alpha.2 <c>postStateValidators</c>, <c>postStateValidatorBalances</c> and
/// <c>postStateValidatorIdentities</c>. The fixture registry has one validator per distinct activation
/// epoch and balance, so a wrong index, field or filter reads a different value.
/// </summary>
public class BeaconStatesValidatorPostTests : BeaconApiFixture
{
    private const string Json = "application/json";
    private const string Octet = "application/octet-stream";

    private const ulong StateEpoch = 412_500;
    private const ulong StateSlot = StateEpoch * 32;
    private const int ValidatorCount = 70;

    private static readonly Hash256 StateRoot = BeaconApiTestHost.TestRoot(0x80);

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet);
        BeaconStateFulu state = BeaconApiTestHost.RichState(BeaconChainSpec.Mainnet, StateSlot);
        state.LatestBlockHeader!.Slot = StateSlot;
        state.Validators = [.. Enumerable.Range(0, ValidatorCount).Select(MakeValidator)];
        state.Balances = [.. Enumerable.Range(0, ValidatorCount).Select(BalanceOf)];
        state.PreviousEpochParticipation = new byte[ValidatorCount];
        state.CurrentEpochParticipation = new byte[ValidatorCount];
        state.InactivityScores = new ulong[ValidatorCount];
        _host.Store.PutState(StateRoot, BeaconStateFulu.Encode(state));
        _host.Store.PutBlock(StateRoot, BeaconApiTestHost.MinimalBlock(StateSlot));
        _host.Store.SetCanonicalRoot(StateSlot, StateRoot);
    }

    [SetUp]
    public void ResetSharedState() => _host.SetStatus(StateRoot, Hash256.Zero, 0);

    /// <summary>The POST form exists to lift the 64-id URI limit of the GET form, so it must accept more than 64 ids.</summary>
    [Test]
    public async Task Validators_post_filters_by_more_than_64_ids_and_by_status()
    {
        string[] ids = [.. Enumerable.Range(0, 66).Select(i => i % 2 == 0 ? i.ToString() : PubkeyOf(i).ToString()), "100000"];
        using HttpResponseMessage all = await Post("validators", JsonSerializer.Serialize(new { ids }));
        JsonElement allData = await ReadEnvelope(all, expectedFinalized: false);
        Assert.That(allData.EnumerateArray().Select(e => e.GetProperty("index").GetString()), Is.EquivalentTo(Enumerable.Range(0, 66).Select(i => i.ToString())));
        JsonElement third = allData.EnumerateArray().Single(e => e.GetProperty("index").GetString() == "3");
        Assert.That(third.GetProperty("balance").GetString(), Is.EqualTo(BalanceOf(3).ToString()));
        Assert.That(third.GetProperty("validator").GetProperty("pubkey").GetString(), Is.EqualTo(PubkeyOf(3).ToString()));

        using HttpResponseMessage pending = await Post("validators", JsonSerializer.Serialize(new { ids, statuses = new[] { "pending_queued" } }));
        JsonElement pendingData = await ReadEnvelope(pending, expectedFinalized: false);
        Assert.That(pendingData.EnumerateArray().Select(e => e.GetProperty("index").GetString()),
            Is.EquivalentTo(Enumerable.Range(0, 66).Where(IsPending).Select(i => i.ToString())));
    }

    [TestCase("{}")]
    [TestCase("{\"ids\":null,\"statuses\":null}")]
    [TestCase("{\"ids\":[],\"statuses\":[]}")]
    public async Task Validators_post_without_filters_returns_the_whole_registry(string body)
    {
        using HttpResponseMessage response = await Post("validators", body);
        JsonElement data = await ReadEnvelope(response, expectedFinalized: false);
        Assert.That(data.GetArrayLength(), Is.EqualTo(ValidatorCount));
    }

    [Test]
    public async Task Validator_balances_post_returns_the_balance_of_each_named_validator()
    {
        string[] ids = [.. Enumerable.Range(0, 66).Reverse().Select(i => i % 3 == 0 ? PubkeyOf(i).ToString() : i.ToString())];
        using HttpResponseMessage response = await Post("validator_balances", JsonSerializer.Serialize(ids));
        JsonElement data = await ReadEnvelope(response, expectedFinalized: false);
        string[] actual = [.. data.EnumerateArray().Select(e => $"{e.GetProperty("index").GetString()}={e.GetProperty("balance").GetString()}")];
        Assert.That(actual, Is.EquivalentTo(Enumerable.Range(0, 66).Select(i => $"{i}={BalanceOf(i)}")));
    }

    [Test]
    public async Task Validator_identities_post_json_lists_index_pubkey_and_activation_epoch()
    {
        using HttpResponseMessage response = await Post("validator_identities", "[\"5\",\"" + PubkeyOf(9) + "\",\"100000\"]");
        JsonElement data = await ReadEnvelope(response, expectedFinalized: false);
        string[] actual = [.. data.EnumerateArray().Select(e => string.Join(';', e.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}")))];
        Assert.That(actual, Is.EquivalentTo(new[]
        {
            $"index=5;pubkey={PubkeyOf(5)};activation_epoch={ActivationOf(5)}",
            $"index=9;pubkey={PubkeyOf(9)};activation_epoch={ActivationOf(9)}",
        }));
    }

    /// <summary>Each SSZ entry is the 64-byte <c>(uint64 index, Bytes48 pubkey, uint64 activation_epoch)</c>, back to back.</summary>
    [Test]
    public async Task Validator_identities_post_ssz_is_the_list_of_fixed_size_entries()
    {
        using HttpResponseMessage response = await Post("validator_identities", "[\"7\",\"2\"]", Octet);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(Octet));

        byte[] expected = new byte[2 * 64];
        int[] order = [7, 2];
        for (int i = 0; i < order.Length; i++)
        {
            Span<byte> entry = expected.AsSpan(i * 64, 64);
            BinaryPrimitives.WriteUInt64LittleEndian(entry, (ulong)order[i]);
            PubkeyOf(order[i]).Bytes.CopyTo(entry[8..]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[56..], ActivationOf(order[i]));
        }

        Assert.That(body, Is.EqualTo(expected));
    }

    /// <summary>
    /// Both POST bodies are uniqueItems; an id repeated, or given once as an index and once as a pubkey, yields one entry,
    /// so a body of repeated ids cannot multiply the response. The GET balances id filter shares the rule.
    /// </summary>
    [Test]
    public async Task Repeated_ids_naming_one_validator_give_one_entry([Values("validator_balances", "validator_identities", "get_validator_balances")] string endpoint)
    {
        string pubkey = PubkeyOf(1).ToString();
        using HttpResponseMessage response = endpoint == "get_validator_balances"
            ? await _host.GetAsync($"/eth/v1/beacon/states/{StateSlot}/validator_balances?id=1&id=1&id={pubkey}", Json)
            : await Post(endpoint, $"[\"1\",\"1\",\"{pubkey}\"]");
        JsonElement data = await ReadEnvelope(response, expectedFinalized: false);
        Assert.That(data.EnumerateArray().Select(e => e.GetProperty("index").GetString()), Is.EqualTo(new[] { "1" }));
    }

    /// <summary>apis/beacon/states/validator_balances.yaml and validator_identities.yaml: an absent or empty body selects every validator.</summary>
    [Test]
    public async Task Optional_body_absent_or_empty_selects_every_validator(
        [Values("validator_balances", "validator_identities")] string endpoint,
        [Values] bool emptyArray)
    {
        using HttpResponseMessage response = emptyArray
            ? await Post(endpoint, "[]")
            : await _host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/eth/v1/beacon/states/{StateSlot}/{endpoint}"));
        JsonElement data = await ReadEnvelope(response, expectedFinalized: false);
        Assert.That(data.EnumerateArray().Select(e => e.GetProperty("index").GetString()), Is.EqualTo(Enumerable.Range(0, ValidatorCount).Select(i => i.ToString())));
    }

    [Test]
    public async Task Finalized_flag_follows_the_finalized_checkpoint([Values("validators", "validator_balances", "validator_identities")] string endpoint)
    {
        _host.SetStatus(StateRoot, StateRoot, StateEpoch);
        using HttpResponseMessage response = await Post(endpoint, endpoint == "validators" ? "{}" : "[]");
        await ReadEnvelope(response, expectedFinalized: true);
    }

    /// <summary>A bad state id or request body is 400, an unretained state 404, a non-JSON body 415 and an unsupported Accept 406.</summary>
    [Test]
    public async Task Errors_follow_the_published_responses(
        [Values("validators", "validator_balances", "validator_identities")] string endpoint,
        [Values("bad_id", "unknown_slot", "unknown_state_root", "bad_validator_id", "malformed_body", "null_body", "not_json", "not_acceptable")] string scenario)
    {
        string ids = endpoint == "validators" ? "{\"ids\":[\"1\"]}" : "[\"1\"]";
        (string stateId, string body, string contentType, string accept, HttpStatusCode expected) = scenario switch
        {
            "bad_id" => ("not-a-state", ids, Json, Json, HttpStatusCode.BadRequest),
            "unknown_slot" => ((StateSlot + 1).ToString(), ids, Json, Json, HttpStatusCode.NotFound),
            "unknown_state_root" => (BeaconApiTestHost.FilledHash(0xee).ToString(), ids, Json, Json, HttpStatusCode.NotFound),
            "bad_validator_id" => (StateSlot.ToString(), ids.Replace("\"1\"", "\"0x12\""), Json, Json, HttpStatusCode.BadRequest),
            "malformed_body" => (StateSlot.ToString(), "{[", Json, Json, HttpStatusCode.BadRequest),
            "null_body" => (StateSlot.ToString(), "null", Json, Json, HttpStatusCode.BadRequest),
            "not_json" => (StateSlot.ToString(), ids, "text/plain", Json, HttpStatusCode.UnsupportedMediaType),
            _ => (StateSlot.ToString(), ids, Json, "text/plain", HttpStatusCode.NotAcceptable),
        };

        using HttpResponseMessage response = await Post(endpoint, body, accept, contentType, stateId);
        await BeaconApiTestHost.AssertErrorAsync(response, expected);
    }

    /// <summary>apis/beacon/states/validators.yaml makes statuses uniqueItems; a repeat would multiply the per-validator matching.</summary>
    [Test]
    public async Task Validators_post_with_a_repeated_status_is_400()
    {
        using HttpResponseMessage response = await Post("validators", "{\"statuses\":[\"active\",\"pending\",\"active\"]}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>apis/beacon/states/validators.yaml marks the POST body required.</summary>
    [Test]
    public async Task Validators_post_without_a_body_is_400()
    {
        using HttpResponseMessage response = await _host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/eth/v1/beacon/states/{StateSlot}/validators"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private Task<HttpResponseMessage> Post(string endpoint, string body, string accept = Json, string contentType = Json, string? stateId = null)
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"/eth/v1/beacon/states/{stateId ?? StateSlot.ToString()}/{endpoint}")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return _host.Client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadEnvelope(HttpResponseMessage response, bool expectedFinalized)
    {
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(Json));

        JsonElement root = JsonDocument.Parse(raw).RootElement;
        Assert.That(root.GetProperty("execution_optimistic").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("finalized").GetBoolean(), Is.EqualTo(expectedFinalized));
        return root.GetProperty("data");
    }

    private static bool IsPending(int i) => i % 4 == 1;

    private static ulong ActivationOf(int i) => IsPending(i) ? StateEpoch + 10 + (ulong)i : 1_000 + (ulong)i;

    private static ulong BalanceOf(int i) => 32_000_000_000 + (ulong)i * 1_000;

    private static BlsPublicKey PubkeyOf(int i)
    {
        byte[] bytes = new byte[48];
        bytes[0] = 0xc0;
        bytes[1] = (byte)i;
        return new BlsPublicKey(bytes);
    }

    private static Validator MakeValidator(int i) => new()
    {
        Pubkey = PubkeyOf(i),
        WithdrawalCredentials = BeaconApiTestHost.FilledHash(0x02),
        EffectiveBalance = 32_000_000_000,
        Slashed = false,
        ActivationEligibilityEpoch = 0,
        ActivationEpoch = ActivationOf(i),
        ExitEpoch = Presets.FarFutureEpoch,
        WithdrawableEpoch = Presets.FarFutureEpoch,
    };
}
