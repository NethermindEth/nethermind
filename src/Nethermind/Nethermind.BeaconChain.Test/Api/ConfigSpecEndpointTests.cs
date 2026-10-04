// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// <c>/eth/v1/config/spec</c> serves every key of <c>configs/mainnet.yaml</c> and of the consensus-specs v1.7.0-beta.2
/// mainnet presets of phase0 to gloas (embedded verbatim in <see cref="PinnedMainnetSpec"/>) and every constant of the
/// specs' Constants tables (<see cref="PinnedSpecConstants"/>), with the network's values where the network differs, so
/// tooling that keys on any of them does not find it missing or wrong.
/// </summary>
public class ConfigSpecEndpointTests
{
    private static readonly Regex ScalarLine = new(@"^(?<key>[A-Z][A-Z0-9_]*):[ \t]*(?<value>[^#\r\n]*?)[ \t]*(#.*)?$", RegexOptions.Multiline);
    private static readonly Regex ScheduleEntry = new(@"^  - EPOCH: (?<epoch>\d+).*\r?\n    MAX_BLOBS_PER_BLOCK: (?<blobs>\d+)", RegexOptions.Multiline);

    /// <summary>The keys this endpoint serves beyond the pinned files: the slot time in seconds that the pinned config replaced by <c>SLOT_DURATION_MS</c>.</summary>
    private static readonly string[] ServedBeyondPinnedFiles = ["SECONDS_PER_SLOT"];

    private static Dictionary<string, string> PinnedScalars() =>
        ScalarLine.Matches(PinnedMainnetSpec.Yaml)
            .Select(m => (Key: m.Groups["key"].Value, Value: Unquote(m.Groups["value"].Value)))
            .Where(entry => entry.Value.Length > 0 && entry.Value != "[]")
            .ToDictionary(entry => entry.Key, entry => entry.Value);

    private static string Unquote(string value) => value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"' ? value[1..^1] : value;

    private static async Task<JsonElement> ServedSpecAsync(BeaconChainSpec spec)
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(spec);
        HttpResponseMessage response = await host.GetAsync("/eth/v1/config/spec", "application/json");
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        return JsonDocument.Parse(raw).RootElement.GetProperty("data").Clone();
    }

    [Test]
    public async Task Serves_every_key_of_the_pinned_mainnet_config_and_presets_with_its_mainnet_value()
    {
        JsonElement served = await ServedSpecAsync(BeaconChainSpec.Mainnet);
        Dictionary<string, string> pinned = PinnedScalars();
        Assert.That(pinned.Count, Is.GreaterThan(150), "the embedded files must parse into every preset and config key");

        Dictionary<string, JsonElement> data = served.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pinned.Keys.Except(data.Keys), Is.Empty, "keys of the pinned files that are not served");
            Assert.That(data.Keys.Except(pinned.Keys).Except(ServedBeyondPinnedFiles).Except(["BLOB_SCHEDULE", "GAS_LIMIT_SCHEDULE"]).Except(PinnedSpecConstants.All.Select(c => c.Name)), Is.Empty, "served keys that no pinned file has");
            Assert.That(pinned.Where(entry => data.TryGetValue(entry.Key, out JsonElement actual) && (actual.ValueKind != JsonValueKind.String || actual.GetString() != entry.Value))
                .Select(entry => $"{entry.Key}={(data.TryGetValue(entry.Key, out JsonElement actual) ? actual.ToString() : "missing")} (pinned {entry.Value})"), Is.Empty);

            MatchCollection schedule = ScheduleEntry.Matches(PinnedMainnetSpec.Yaml);
            Assert.That(schedule, Is.Not.Empty);
            Assert.That(data["BLOB_SCHEDULE"].EnumerateArray().Select(e => (e.GetProperty("EPOCH").GetString(), e.GetProperty("MAX_BLOBS_PER_BLOCK").GetString())),
                Is.EqualTo(schedule.Select(m => (m.Groups["epoch"].Value, m.Groups["blobs"].Value))));
            Assert.That(data["GAS_LIMIT_SCHEDULE"].GetArrayLength(), Is.Zero);
        }
    }

    private const string ArrayConstant = "PARTICIPATION_FLAG_WEIGHTS";

    /// <summary>Renders a served constant the way getSpec requires: a string, except the weights array whose elements are strings; any other JSON kind is reported by name.</summary>
    private static string Rendered(string name, JsonElement value) =>
        name == ArrayConstant
            ? value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) ? string.Join(',', value.EnumerateArray().Select(e => e.GetString())) : $"<{value.ValueKind}>"
            : value.ValueKind == JsonValueKind.String ? value.GetString()! : $"<{value.ValueKind}>";

    private static IEnumerable<string> ConstantMismatches(JsonElement served) =>
        PinnedSpecConstants.All.Select(c => served.TryGetProperty(c.Name, out JsonElement actual) ? (Name: c.Name, Actual: Rendered(c.Name, actual), Pinned: c.Value) : (Name: c.Name, Actual: "missing", Pinned: c.Value))
            .Where(c => c.Actual != c.Pinned)
            .Select(c => $"{c.Name}={c.Actual} (pinned {c.Pinned})");

    [Test]
    public async Task Serves_every_constant_of_the_specs_constants_tables_with_its_pinned_value()
    {
        JsonElement served = await ServedSpecAsync(BeaconChainSpec.Mainnet);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PinnedSpecConstants.All.Length, Is.GreaterThan(80));
            Assert.That(ConstantMismatches(served), Is.Empty);
        }
    }

    [TestCase("DOMAIN_BEACON_PROPOSER", "0x00000000")]
    [TestCase("DOMAIN_BEACON_ATTESTER", "0x01000000")]
    [TestCase("DOMAIN_BUILDER_DEPOSIT", "0x0e000000")]
    [TestCase("FAR_FUTURE_EPOCH", "18446744073709551615")]
    [TestCase("GENESIS_SLOT", "0")]
    [TestCase("GENESIS_EPOCH", "0")]
    [TestCase("BLS_WITHDRAWAL_PREFIX", "0x00")]
    [TestCase("ETH1_ADDRESS_WITHDRAWAL_PREFIX", "0x01")]
    [TestCase("COMPOUNDING_WITHDRAWAL_PREFIX", "0x02")]
    [TestCase("TARGET_AGGREGATORS_PER_COMMITTEE", "16")]
    [TestCase("PARTICIPATION_FLAG_WEIGHTS", "14,26,14")]
    public async Task Serves_the_constants_tooling_keys_on_in_the_getSpec_format(string key, string expected)
    {
        JsonElement served = await ServedSpecAsync(BeaconChainSpec.Mainnet);

        Assert.That(Rendered(key, served.GetProperty(key)), Is.EqualTo(expected));
    }

    /// <summary>The keys of the pinned <c>presets/mainnet/{heze,eip8148,eip8205,eip8321}.yaml</c>, which describe forks this node does not run.</summary>
    private static readonly string[] PresetKeysOfForksNotRun =
    [
        "INCLUSION_LIST_COMMITTEE_SIZE", "MAX_SIGNED_EXECUTION_PAYLOAD_BID_SIZE_HEZE", "MAX_SIGNED_INCLUSION_LIST_SIZE",
        "MAX_SET_SWEEP_THRESHOLD_REQUESTS_PER_PAYLOAD", "MAX_PREREGISTRATION_REQUESTS_PER_PAYLOAD", "PREREGISTRATIONS_LIMIT",
        "PREREGISTRATION_EXPIRY_SLOTS", "COMMITMENT_REGISTRATION_DELAY", "MAX_RANDAO_COMMITMENT_REGISTRATIONS",
    ];

    // A preset value is only served for a fork whose types this node builds, so it never claims a limit it does not enforce.
    [Test]
    public async Task Does_not_serve_the_preset_keys_of_forks_the_node_does_not_run()
    {
        JsonElement served = await ServedSpecAsync(BeaconChainSpec.Mainnet);

        Assert.That(served.EnumerateObject().Select(p => p.Name).Intersect(PresetKeysOfForksNotRun), Is.Empty);
    }

    [Test]
    public async Task Serves_the_values_the_state_transition_reads_for_every_key_that_has_a_constant()
    {
        JsonElement served = await ServedSpecAsync(BeaconChainSpec.Mainnet);
        Dictionary<string, string> data = served.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!);

        List<string> compared = [];
        List<string> mismatched = [];
        foreach (FieldInfo constant in typeof(Presets).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType != typeof(byte)))
        {
            string key = Regex.Replace(constant.Name, "(?<=[a-z0-9])(?=[A-Z])", "_").ToUpperInvariant();
            if (!data.TryGetValue(key, out string? actual)) continue;

            compared.Add(key);
            string expected = Convert.ToString(constant.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)!;
            if (actual != expected) mismatched.Add($"{key}: served {actual}, Presets.{constant.Name} = {expected}");
        }

        Assert.That(compared, Has.Count.GreaterThan(60), "the comparison must reach most preset constants");
        Assert.That(mismatched, Is.Empty);
    }

    // eth-clients hoodi and sepolia metadata/config.yaml.
    private static IEnumerable<TestCaseData> NetworkValues()
    {
        (string Key, string Value)[] hoodi =
        [
            ("CONFIG_NAME", "hoodi"), ("PRESET_BASE", "mainnet"), ("GENESIS_FORK_VERSION", "0x10000910"), ("MIN_GENESIS_TIME", "1742212800"),
            ("GENESIS_DELAY", "600"), ("TERMINAL_TOTAL_DIFFICULTY", "0"), ("MIN_GENESIS_ACTIVE_VALIDATOR_COUNT", "16384"), ("SECONDS_PER_ETH1_BLOCK", "12"),
            ("ALTAIR_FORK_VERSION", "0x20000910"), ("ALTAIR_FORK_EPOCH", "0"), ("DENEB_FORK_VERSION", "0x50000910"), ("ELECTRA_FORK_VERSION", "0x60000910"),
            ("ELECTRA_FORK_EPOCH", "2048"), ("FULU_FORK_VERSION", "0x70000910"), ("FULU_FORK_EPOCH", "50688"),
            ("GLOAS_FORK_EPOCH", "18446744073709551615"), ("DEPOSIT_CHAIN_ID", "560048"), ("DEPOSIT_NETWORK_ID", "560048"),
            ("DEPOSIT_CONTRACT_ADDRESS", "0x00000000219ab540356cBB839Cbe05303d7705Fa"), ("SLOT_DURATION_MS", "12000"),
        ];
        (string Key, string Value)[] sepolia =
        [
            ("CONFIG_NAME", "sepolia"), ("PRESET_BASE", "mainnet"), ("GENESIS_FORK_VERSION", "0x90000069"), ("MIN_GENESIS_TIME", "1655647200"),
            ("GENESIS_DELAY", "86400"), ("TERMINAL_TOTAL_DIFFICULTY", "17000000000000000"), ("MIN_GENESIS_ACTIVE_VALIDATOR_COUNT", "1300"), ("SECONDS_PER_ETH1_BLOCK", "14"),
            ("ALTAIR_FORK_VERSION", "0x90000070"), ("ALTAIR_FORK_EPOCH", "50"), ("CAPELLA_FORK_EPOCH", "56832"), ("ELECTRA_FORK_VERSION", "0x90000074"),
            ("FULU_FORK_EPOCH", "272640"), ("GLOAS_FORK_VERSION", "0x90000076"), ("GLOAS_FORK_EPOCH", "353024"),
            ("DEPOSIT_CHAIN_ID", "11155111"), ("DEPOSIT_NETWORK_ID", "11155111"), ("DEPOSIT_CONTRACT_ADDRESS", "0x7f02C3E3c98b133055B8B348B2Ac625669Ed295D"),
        ];
        foreach ((string key, string value) in hoodi) yield return new TestCaseData("hoodi", key, value).SetName($"Hoodi serves its own {key}");
        foreach ((string key, string value) in sepolia) yield return new TestCaseData("sepolia", key, value).SetName($"Sepolia serves its own {key}");
    }

    [TestCaseSource(nameof(NetworkValues))]
    public async Task Serves_the_values_of_the_network_it_runs_on(string network, string key, string expected)
    {
        JsonElement served = await ServedSpecAsync(network == "hoodi" ? BeaconChainSpec.Hoodi : BeaconChainSpec.Sepolia);

        Assert.That(served.GetProperty(key).GetString(), Is.EqualTo(expected));
    }

    [Test]
    public async Task Every_network_serves_the_blob_schedule_of_its_own_spec_and_every_pinned_key([Values("hoodi", "sepolia")] string network)
    {
        BeaconChainSpec spec = network == "hoodi" ? BeaconChainSpec.Hoodi : BeaconChainSpec.Sepolia;
        JsonElement served = await ServedSpecAsync(spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PinnedScalars().Keys.Except(served.EnumerateObject().Select(p => p.Name)), Is.Empty);
            Assert.That(ConstantMismatches(served), Is.Empty);
            Assert.That(served.GetProperty("BLOB_SCHEDULE").EnumerateArray().Select(e => (e.GetProperty("EPOCH").GetString(), e.GetProperty("MAX_BLOBS_PER_BLOCK").GetString())),
                Is.EqualTo(spec.BlobSchedule.Select(e => (e.Epoch.ToString(), e.MaxBlobsPerBlock.ToString()))));
        }
    }
}
