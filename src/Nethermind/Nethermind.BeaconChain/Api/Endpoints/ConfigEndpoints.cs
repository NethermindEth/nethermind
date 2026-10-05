// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary><c>/eth/v1/config/*</c>: spec constants, fork schedule, deposit contract.</summary>
internal static class ConfigEndpoints
{
    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapGet("/eth/v1/config/spec", c => ContentNegotiation.JsonOnly(c, ctx, Spec));
        app.MapGet("/eth/v1/config/fork_schedule", c => ContentNegotiation.JsonOnly(c, ctx, ForkSchedule));
        app.MapGet("/eth/v1/config/deposit_contract", c => ContentNegotiation.JsonOnly(c, ctx, DepositContract));
    }

    /// <summary>The network config's <c>DEPOSIT_CONTRACT_ADDRESS</c>, or <c>null</c> for a chain this driver has no network config for.</summary>
    /// <remarks>
    /// From consensus-specs v1.7.0-beta.2 <c>configs/mainnet.yaml</c> and the eth-clients <c>hoodi</c> and
    /// <c>sepolia</c> <c>metadata/config.yaml</c>, whose <c>DEPOSIT_CHAIN_ID</c> equals the chain id each is keyed by.
    /// </remarks>
    private static string? DepositContractAddress(ulong chainId) => chainId switch
    {
        BlockchainIds.Mainnet => "0x00000000219ab540356cBB839Cbe05303d7705Fa",
        BlockchainIds.Hoodi => "0x00000000219ab540356cBB839Cbe05303d7705Fa",
        BlockchainIds.Sepolia => "0x7f02C3E3c98b133055B8B348B2Ac625669Ed295D",
        _ => null,
    };

    private static Task DepositContract(HttpContext c, BeaconApiContext ctx)
    {
        if (DepositContractAddress(ctx.Spec.ChainId) is not { } address)
        {
            return ApiErrors.Write(c, StatusCodes.Status500InternalServerError,
                $"No deposit contract is configured for chain {ctx.Spec.ChainId}.", c.RequestAborted);
        }

        return BeaconApiJson.WriteDataAsync(c, new DepositContractDto(ctx.Spec.ChainId.ToString(), address), c.RequestAborted);
    }

    /// <remarks>
    /// Every key of the consensus-specs v1.7.0-beta.2 mainnet <c>config.yaml</c> and of the presets of phase0 to gloas, as the beacon-APIs
    /// <c>getSpec</c> asks for: numbers as decimal strings, <c>0x</c> values as hex, schedules as arrays. The mainnet
    /// values come from <see cref="SpecValues.Mainnet"/> and the network's own replace them, and the constants of the specs'
    /// Constants tables come from <see cref="SpecConstants.All"/>; every network runs the
    /// mainnet preset. <c>SECONDS_PER_SLOT</c>, which that config no longer lists, stays for tooling that still reads it.
    /// </remarks>
    private static Task Spec(HttpContext c, BeaconApiContext ctx)
    {
        BeaconChainSpec spec = ctx.Spec;
        Dictionary<string, object> data = new(SpecValues.Mainnet.Count + SpecConstants.All.Count + 4);
        foreach ((string key, string value) in SpecValues.Mainnet)
        {
            data[key] = value;
        }

        foreach ((string key, object value) in SpecConstants.All)
        {
            data[key] = value;
        }

        ApplyChain(data, spec.ChainId);
        data["SLOTS_PER_EPOCH"] = spec.SlotsPerEpoch.ToString();
        data["SECONDS_PER_SLOT"] = spec.SecondsPerSlot.ToString();
        data["SLOT_DURATION_MS"] = (spec.SecondsPerSlot * 1000).ToString();
        data["GENESIS_FORK_VERSION"] = spec.Forks[0].Version.ToHexString(withZeroX: true);
        string[] earlyForks = ["ALTAIR", "BELLATRIX", "CAPELLA", "DENEB"];
        for (int i = 0; i < earlyForks.Length && i + 1 < spec.Forks.Length; i++)
        {
            data[$"{earlyForks[i]}_FORK_VERSION"] = spec.Forks[i + 1].Version.ToHexString(withZeroX: true);
            data[$"{earlyForks[i]}_FORK_EPOCH"] = spec.Forks[i + 1].Epoch.ToString();
        }

        data["ELECTRA_FORK_EPOCH"] = spec.ElectraForkEpoch.ToString();
        data["ELECTRA_FORK_VERSION"] = spec.VersionForEpoch(spec.ElectraForkEpoch).ToHexString(withZeroX: true);
        data["FULU_FORK_EPOCH"] = spec.FuluForkEpoch.ToString();
        data["FULU_FORK_VERSION"] = spec.VersionForEpoch(spec.FuluForkEpoch).ToHexString(withZeroX: true);
        data["GLOAS_FORK_EPOCH"] = spec.GloasForkEpoch.ToString();
        data["GLOAS_FORK_VERSION"] = spec.GloasForkVersion.ToHexString(withZeroX: true);
        data["MAX_BLOBS_PER_BLOCK_ELECTRA"] = spec.MaxBlobsPerBlockElectra.ToString();
        data["BLOB_SCHEDULE"] = spec.BlobSchedule
            .Select(entry => new Dictionary<string, string> { ["EPOCH"] = entry.Epoch.ToString(), ["MAX_BLOBS_PER_BLOCK"] = entry.MaxBlobsPerBlock.ToString() })
            .ToArray();
        data["GAS_LIMIT_SCHEDULE"] = Array.Empty<string>();

        return BeaconApiJson.WriteDataAsync(c, data, c.RequestAborted);
    }

    /// <summary>Replaces the mainnet values of the keys that differ per network and that <see cref="BeaconChainSpec"/> does not hold.</summary>
    /// <remarks>From the eth-clients <c>hoodi</c> and <c>sepolia</c> <c>metadata/config.yaml</c>; a chain with no entry there keeps the mainnet values.</remarks>
    private static void ApplyChain(Dictionary<string, object> data, ulong chainId)
    {
        if (DepositContractAddress(chainId) is { } depositContract)
        {
            data["DEPOSIT_CHAIN_ID"] = chainId.ToString();
            data["DEPOSIT_NETWORK_ID"] = chainId.ToString();
            data["DEPOSIT_CONTRACT_ADDRESS"] = depositContract;
        }

        switch (chainId)
        {
            case BlockchainIds.Hoodi:
                data["CONFIG_NAME"] = "hoodi";
                data["MIN_GENESIS_TIME"] = "1742212800";
                data["GENESIS_DELAY"] = "600";
                data["TERMINAL_TOTAL_DIFFICULTY"] = "0";
                data["SECONDS_PER_ETH1_BLOCK"] = "12";
                break;
            case BlockchainIds.Sepolia:
                data["CONFIG_NAME"] = "sepolia";
                data["MIN_GENESIS_ACTIVE_VALIDATOR_COUNT"] = "1300";
                data["MIN_GENESIS_TIME"] = "1655647200";
                data["GENESIS_DELAY"] = "86400";
                data["TERMINAL_TOTAL_DIFFICULTY"] = "17000000000000000";
                break;
        }
    }

    private static Task ForkSchedule(HttpContext c, BeaconApiContext ctx)
    {
        ForkScheduleEntry[] forks = ctx.Spec.Forks;
        ForkDto[] data = new ForkDto[forks.Length];
        for (int i = 0; i < forks.Length; i++)
        {
            byte[] previousVersion = i == 0 ? forks[0].Version : forks[i - 1].Version;
            data[i] = new ForkDto(
                previousVersion.ToHexString(withZeroX: true),
                forks[i].Version.ToHexString(withZeroX: true),
                forks[i].Epoch.ToString());
        }

        return BeaconApiJson.WriteDataAsync(c, data, c.RequestAborted);
    }

    private sealed record DepositContractDto(
        [property: JsonPropertyName("chain_id")] string ChainId,
        [property: JsonPropertyName("address")] string Address);

    private sealed record ForkDto(
        [property: JsonPropertyName("previous_version")] string PreviousVersion,
        [property: JsonPropertyName("current_version")] string CurrentVersion,
        [property: JsonPropertyName("epoch")] string Epoch);
}
