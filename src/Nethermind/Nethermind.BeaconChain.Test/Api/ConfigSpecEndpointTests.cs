// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// <c>/eth/v1/config/spec</c> reports the mainnet preset values that state transition reads, under
/// their consensus-specs v1.7.0-beta.2 names, so tooling that keys on them does not find them missing.
/// </summary>
public class ConfigSpecEndpointTests
{
    // Literals from consensus-specs v1.7.0-beta.2 presets/mainnet (phase0 to electra).
    private static readonly Dictionary<string, string> MainnetPreset = new()
    {
        ["SHUFFLE_ROUND_COUNT"] = "90",
        ["HYSTERESIS_QUOTIENT"] = "4",
        ["HYSTERESIS_DOWNWARD_MULTIPLIER"] = "1",
        ["HYSTERESIS_UPWARD_MULTIPLIER"] = "5",
        ["MIN_DEPOSIT_AMOUNT"] = "1000000000",
        ["MAX_EFFECTIVE_BALANCE"] = "32000000000",
        ["EFFECTIVE_BALANCE_INCREMENT"] = "1000000000",
        ["MAX_DEPOSITS"] = "16",
        ["MIN_ATTESTATION_INCLUSION_DELAY"] = "1",
        ["EPOCHS_PER_ETH1_VOTING_PERIOD"] = "64",
        ["SLOTS_PER_HISTORICAL_ROOT"] = "8192",
        ["MIN_EPOCHS_TO_INACTIVITY_PENALTY"] = "4",
        ["EPOCHS_PER_HISTORICAL_VECTOR"] = "65536",
        ["EPOCHS_PER_SLASHINGS_VECTOR"] = "8192",
        ["BASE_REWARD_FACTOR"] = "64",
        ["SYNC_COMMITTEE_SIZE"] = "512",
        ["EPOCHS_PER_SYNC_COMMITTEE_PERIOD"] = "256",
        ["INACTIVITY_PENALTY_QUOTIENT_BELLATRIX"] = "16777216",
        ["PROPORTIONAL_SLASHING_MULTIPLIER_BELLATRIX"] = "3",
        ["MAX_WITHDRAWALS_PER_PAYLOAD"] = "16",
        ["MAX_VALIDATORS_PER_WITHDRAWALS_SWEEP"] = "16384",
        ["MIN_SLASHING_PENALTY_QUOTIENT_ELECTRA"] = "4096",
        ["WHISTLEBLOWER_REWARD_QUOTIENT_ELECTRA"] = "4096",
        ["PENDING_PARTIAL_WITHDRAWALS_LIMIT"] = "134217728",
        ["PENDING_CONSOLIDATIONS_LIMIT"] = "262144",
        ["MAX_PENDING_PARTIALS_PER_WITHDRAWALS_SWEEP"] = "8",
        ["MAX_PENDING_DEPOSITS_PER_EPOCH"] = "16",
        ["MAX_PROPOSER_SLASHINGS"] = "16",
        ["MAX_ATTESTER_SLASHINGS_ELECTRA"] = "1",
        ["MAX_ATTESTATIONS_ELECTRA"] = "8",
        ["MAX_VOLUNTARY_EXITS"] = "16",
        ["MAX_BLS_TO_EXECUTION_CHANGES"] = "16",
        ["MAX_DEPOSIT_REQUESTS_PER_PAYLOAD"] = "8192",
        ["MAX_WITHDRAWAL_REQUESTS_PER_PAYLOAD"] = "16",
        ["MAX_CONSOLIDATION_REQUESTS_PER_PAYLOAD"] = "2",
        ["MAX_PER_EPOCH_ACTIVATION_CHURN_LIMIT"] = "8",
        ["MIN_PER_EPOCH_CHURN_LIMIT_ELECTRA"] = "128000000000",
    };

    [Test]
    public async Task Reports_each_mainnet_preset_value_as_a_decimal_string()
    {
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet);

        HttpResponseMessage response = await host.GetAsync("/eth/v1/config/spec", "application/json");
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw);
        Dictionary<string, string> data = JsonDocument.Parse(raw).RootElement.GetProperty("data").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);

        Assert.That(MainnetPreset.Where(expected => !data.TryGetValue(expected.Key, out string? actual) || actual != expected.Value)
            .Select(expected => $"{expected.Key}={data.GetValueOrDefault(expected.Key)} (expected {expected.Value})"), Is.Empty);
    }
}
